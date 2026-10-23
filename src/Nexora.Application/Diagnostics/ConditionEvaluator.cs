using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexora.Application.Diagnostics;

/// <summary>Three-valued so that missing data can never make a negated rule fire: <c>not(unknown)</c> is still unknown.</summary>
public enum Tri { False, True, Unknown }

/// <summary>
/// Evaluates a rule's condition over collector JSON. Safe by construction: it only walks <see cref="JsonElement"/>s along a parsed
/// <see cref="DiagPath"/> (no reflection, no dynamic code), regular expressions come from the loaded profile and always carry a timeout,
/// recursion depth and path fan-out are bounded, numbers are read with the invariant culture, and absent or mistyped data yields
/// <see cref="Tri.Unknown"/>, never an exception.
/// </summary>
public static class ConditionEvaluator
{
    /// <summary>Resolves a source name to its JSON; null when that collector delivered nothing usable (missing, incomplete or failed).</summary>
    public delegate JsonElement? SourceLookup(string source);

    public static Tri Evaluate(Condition condition, SourceLookup sources) => Eval(condition, sources, 0);

    private static Tri Eval(Condition c, SourceLookup sources, int depth)
    {
        if (depth > DiagLimits.MaxConditionDepth) return Tri.Unknown;
        switch (c)
        {
            case AllOf all:
            {
                var unknown = false;
                foreach (var item in all.Items)
                {
                    var r = Eval(item, sources, depth + 1);
                    if (r == Tri.False) return Tri.False;
                    if (r == Tri.Unknown) unknown = true;
                }
                return unknown ? Tri.Unknown : Tri.True;
            }
            case AnyOf any:
            {
                var unknown = false;
                foreach (var item in any.Items)
                {
                    var r = Eval(item, sources, depth + 1);
                    if (r == Tri.True) return Tri.True;
                    if (r == Tri.Unknown) unknown = true;
                }
                return unknown ? Tri.Unknown : Tri.False;
            }
            case NotOf not:
                return Eval(not.Inner, sources, depth + 1) switch { Tri.True => Tri.False, Tri.False => Tri.True, _ => Tri.Unknown };
            case Leaf leaf:
                return EvalLeaf(leaf, sources);
            default:
                return Tri.Unknown;
        }
    }

    private static Tri EvalLeaf(Leaf leaf, SourceLookup sources)
    {
        JsonElement? root;
        try { root = sources(leaf.Path.Source); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return Tri.Unknown; }
        if (root is not { } doc) return Tri.Unknown;

        Func<JsonElement, bool>? filter = null;
        if (leaf.Join is { } join && (filter = JoinFilter(join, sources)) is null) return Tri.Unknown;
        var found = leaf.Path.Resolve(doc, filter);

        if (leaf.Op == DiagOps.Exists)
        {
            var want = leaf.Literal is not { ValueKind: JsonValueKind.False };
            return (found.Values.Count > 0) == want ? Tri.True : Tri.False;
        }
        if (found.Values.Count == 0) return found.Absent > 0 ? Tri.Unknown : Tri.False; // an empty array is a real answer: nothing matches

        var comparable = 0;
        foreach (var value in found.Values)
        {
            var r = Compare(leaf, value);
            if (r == Tri.Unknown) continue;
            comparable++;
            if (r == Tri.True) return Tri.True;
        }
        return comparable > 0 ? Tri.False : Tri.Unknown;
    }

    /// <summary>
    /// The element filter of a join, or null when the other side is not available (its collector delivered nothing, or no value could be read).
    /// An empty set (for example no OST file at all) is a real answer: the filter then matches nothing.
    /// </summary>
    public static Func<JsonElement, bool>? JoinFilter(JoinSpec join, SourceLookup sources)
    {
        JsonElement? other;
        try { other = sources(join.With.Source); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
        if (other is not { } doc) return null;
        var values = join.With.Resolve(doc);
        if (values.Missing) return null;
        var set = values.Values.Where(v => v.ValueKind == JsonValueKind.String).Select(v => NormalizeKey(v.GetString())).ToHashSet(StringComparer.Ordinal);
        return item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(join.Key, out var k) && k.ValueKind == JsonValueKind.String && set.Contains(NormalizeKey(k.GetString()));
    }

    private static string NormalizeKey(string? s) => (s ?? "").Trim().TrimEnd(':', '\\').ToUpperInvariant();

    private static Tri Compare(Leaf leaf, JsonElement value)
    {
        var lit = leaf.Literal;
        switch (leaf.Op)
        {
            case DiagOps.CountGt:
                return value.ValueKind == JsonValueKind.Array && lit is { } n && TryNumber(n, out var limit)
                    ? FromBool(value.GetArrayLength() > limit) : Tri.Unknown;
            case DiagOps.Gt or DiagOps.Gte or DiagOps.Lt or DiagOps.Lte:
            {
                if (lit is not { } l || !TryNumber(l, out var rhs) || !TryNumber(value, out var lhs)) return Tri.Unknown;
                return FromBool(leaf.Op switch { DiagOps.Gt => lhs > rhs, DiagOps.Gte => lhs >= rhs, DiagOps.Lt => lhs < rhs, _ => lhs <= rhs });
            }
            case DiagOps.Eq:
                return lit is { } e && Equal(value, e) is { } eq ? FromBool(eq) : Tri.Unknown;
            case DiagOps.Ne:
                return lit is { } ne && Equal(value, ne) is { } same ? FromBool(!same) : Tri.Unknown;
            case DiagOps.In:
            {
                if (lit is not { ValueKind: JsonValueKind.Array } list) return Tri.Unknown;
                var typed = false;
                foreach (var item in list.EnumerateArray())
                {
                    var eq2 = Equal(value, item);
                    if (eq2 is null) continue;
                    typed = true;
                    if (eq2 == true) return Tri.True;
                }
                return typed ? Tri.False : Tri.Unknown;
            }
            case DiagOps.Contains:
            {
                if (lit is not { } needle) return Tri.Unknown;
                if (value.ValueKind == JsonValueKind.String && needle.ValueKind == JsonValueKind.String)
                    return FromBool((value.GetString() ?? "").Contains(needle.GetString() ?? "", StringComparison.OrdinalIgnoreCase));
                if (value.ValueKind == JsonValueKind.Array)
                {
                    var typed = false;
                    foreach (var item in value.EnumerateArray())
                    {
                        var eq3 = Equal(item, needle);
                        if (eq3 is null) continue;
                        typed = true;
                        if (eq3 == true) return Tri.True;
                    }
                    return typed || value.GetArrayLength() == 0 ? Tri.False : Tri.Unknown;
                }
                return Tri.Unknown;
            }
            case DiagOps.AnyMatch:
            {
                if (leaf.Pattern is null || value.ValueKind != JsonValueKind.String) return Tri.Unknown;
                var text = value.GetString() ?? "";
                if (text.Length > DiagLimits.MaxCompareLength) text = text[..DiagLimits.MaxCompareLength];
                try { return FromBool(leaf.Pattern.IsMatch(text)); }
                catch (RegexMatchTimeoutException) { return Tri.Unknown; }
            }
            default:
                return Tri.Unknown;
        }
    }

    private static Tri FromBool(bool b) => b ? Tri.True : Tri.False;

    /// <summary>True/false when the two are comparable; null on a type mismatch. Strings compare ignoring case (Windows names), numbers numerically.</summary>
    private static bool? Equal(JsonElement value, JsonElement literal)
    {
        switch (literal.ValueKind)
        {
            case JsonValueKind.Number:
                return TryNumber(value, out var a) && TryNumber(literal, out var b) ? a == b : null;
            case JsonValueKind.String:
                return value.ValueKind == JsonValueKind.String
                    ? string.Equals(value.GetString(), literal.GetString(), StringComparison.OrdinalIgnoreCase) : null;
            case JsonValueKind.True or JsonValueKind.False:
            {
                var want = literal.ValueKind == JsonValueKind.True;
                return value.ValueKind switch
                {
                    JsonValueKind.True => want,
                    JsonValueKind.False => !want,
                    JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed == want,
                    _ => null,
                };
            }
            default:
                return null;
        }
    }

    /// <summary>A JSON number, or a string holding one (read with the invariant culture: the fleet runs mixed locales). NaN and infinity are refused.</summary>
    public static bool TryNumber(JsonElement e, out double number)
    {
        number = 0;
        switch (e.ValueKind)
        {
            case JsonValueKind.Number:
                return e.TryGetDouble(out number) && double.IsFinite(number);
            case JsonValueKind.String:
                var s = e.GetString();
                return s is { Length: > 0 and <= 40 } && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
            default:
                return false;
        }
    }

    /// <summary>Every source a condition reads, for the engine's "is the data there" check.</summary>
    public static IEnumerable<string> Sources(Condition c) => c switch
    {
        AllOf a => a.Items.SelectMany(Sources),
        AnyOf a => a.Items.SelectMany(Sources),
        NotOf n => Sources(n.Inner),
        Leaf l => l.Join is null ? [l.Path.Source] : [l.Path.Source, l.Join.With.Source],
        _ => [],
    };
}
