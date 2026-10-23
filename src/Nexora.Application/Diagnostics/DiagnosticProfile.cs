using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexora.Application.Diagnostics;

/// <summary>Hard limits of the rule language. A profile that exceeds one is rejected when it is loaded.</summary>
public static class DiagLimits
{
    public const int MaxRules = 100, MaxCollectors = 32, MaxConditionDepth = 8, MaxConditionNodes = 64, MaxEvidencePerRule = 10, MaxActionsPerRule = 5;
    public const int MaxPathSegments = 8, MaxInList = 50, MaxPatternLength = 200, MaxCompareLength = 1_000;
    /// <summary>Most elements one path expression may visit; a bigger array is cut, never walked in full.</summary>
    public const int MaxNodesPerPath = 2_000;
    public const int MaxEvidenceValues = 8;
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);
}

/// <summary>A symptom profile: which collectors to run and which rules to apply to what they return. Loaded from data, never built from a request.</summary>
public sealed record DiagProfile(
    string Id, string Name, string Description, int Version,
    IReadOnlyList<DiagCollectorRef> Collectors, IReadOnlyList<DiagRule> Rules);

public sealed record DiagCollectorRef(string Id, bool Required, int TimeoutSeconds);

public sealed record DiagRule(
    string Id, string Cause, string Severity, string Confidence, Condition When,
    IReadOnlyList<EvidenceSpec> Evidence, IReadOnlyList<ActionSpec> Actions, IReadOnlyList<DiagPath>? Needs = null);

/// <param name="Format">text, bytes, percent, count or number.</param>
public sealed record EvidenceSpec(string Label, DiagPath Path, string Format, JoinSpec? Join = null);

/// <summary>
/// Keeps only the elements of the path's LAST array step whose <see cref="Key"/> property equals one of the values found at <see cref="With"/>
/// (letters compare without a trailing colon or backslash, ignoring case). It lets a rule relate two collectors, for example the volume that holds the OST file.
/// </summary>
public sealed record JoinSpec(string Key, DiagPath With);

public sealed record ActionSpec(string Kind, string Label, string? ActionCode, IReadOnlyDictionary<string, JsonElement>? Params, string? Instructions);

public abstract record Condition;
public sealed record AllOf(IReadOnlyList<Condition> Items) : Condition;
public sealed record AnyOf(IReadOnlyList<Condition> Items) : Condition;
public sealed record NotOf(Condition Inner) : Condition;

/// <param name="Literal">The value to compare with (a string, number or bool; an array for <c>in</c>), cloned so it outlives its parse.</param>
/// <param name="Pattern">Compiled regular expression for <c>anyMatch</c>; always has a match timeout.</param>
public sealed record Leaf(DiagPath Path, string Op, JsonElement? Literal, Regex? Pattern, JoinSpec? Join = null) : Condition;

public static class DiagOps
{
    public const string Eq = "eq", Ne = "ne", Gt = "gt", Gte = "gte", Lt = "lt", Lte = "lte", In = "in", Contains = "contains",
        Exists = "exists", CountGt = "countGt", AnyMatch = "anyMatch";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        { Eq, Ne, Gt, Gte, Lt, Lte, In, Contains, Exists, CountGt, AnyMatch };
}

/// <summary>
/// An address into one collector's JSON: <c>outlook-files.files[*].sizeBytes</c>. The first part names the source (a collector id, or a server-side
/// source such as <c>server:diskHealth</c>); the rest is property names, each of which may end in <c>[*]</c> to step into every element of an array,
/// or in <c>[field=value]</c> to step into the elements whose field equals a literal (<c>files[kind=ost]</c>).
/// Nothing else is allowed: no indexes, expressions, functions or reflection.
/// </summary>
public sealed record DiagPath(string Source, IReadOnlyList<PathSegment> Segments, string Text)
{
    public const string DiskHealthSource = "server:diskHealth";
    public static readonly IReadOnlySet<string> ServerSources = new HashSet<string>(StringComparer.Ordinal) { DiskHealthSource };

    private static readonly Regex SourcePattern = new(@"^(?:[a-z0-9][a-z0-9\-]{0,63}|server:[A-Za-z][A-Za-z0-9]{0,31})\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex SegmentPattern = new(@"^([A-Za-z_][A-Za-z0-9_]{0,63})(?:\[(?:(\*)|([A-Za-z_][A-Za-z0-9_]{0,63})=([A-Za-z0-9_.\-]{1,64}))\])?\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static bool TryParse(string? text, out DiagPath path, out string error)
    {
        path = null!; error = "";
        if (string.IsNullOrWhiteSpace(text) || text.Length > 200) { error = "the path is empty or too long"; return false; }
        var parts = text.Split('.');
        if (!SourcePattern.IsMatch(parts[0])) { error = $"'{parts[0]}' is not a valid source name"; return false; }
        if (parts.Length - 1 > DiagLimits.MaxPathSegments) { error = "the path has too many parts"; return false; }
        var segments = new List<PathSegment>();
        foreach (var part in parts.Skip(1))
        {
            var m = SegmentPattern.Match(part);
            if (!m.Success) { error = $"'{part}' is not a valid path part (use names, [*] and [field=value] only)"; return false; }
            segments.Add(m.Groups[3].Success
                ? new PathSegment(m.Groups[1].Value, true, m.Groups[3].Value, m.Groups[4].Value)
                : new PathSegment(m.Groups[1].Value, m.Groups[2].Success));
        }
        path = new DiagPath(parts[0], segments, text);
        return true;
    }

    /// <summary>
    /// Walks the path over <paramref name="root"/>. Absent properties and non-array wildcards are counted, never thrown;
    /// the walk visits at most <see cref="DiagLimits.MaxNodesPerPath"/> elements per step.
    /// </summary>
    public PathValues Resolve(JsonElement root, Func<JsonElement, bool>? lastArrayFilter = null)
    {
        var current = new List<JsonElement> { root };
        var absent = 0;
        var truncated = false;
        var lastWildcard = -1;
        for (var i = 0; i < Segments.Count; i++) if (Segments[i].Wildcard) lastWildcard = i;
        for (var step = 0; step < Segments.Count; step++)
        {
            var seg = Segments[step];
            var next = new List<JsonElement>();
            foreach (var el in current)
            {
                if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(seg.Name, out var v) || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) { absent++; continue; }
                if (!seg.Wildcard) { if (next.Count < DiagLimits.MaxNodesPerPath) next.Add(v); else truncated = true; continue; }
                if (v.ValueKind != JsonValueKind.Array) { absent++; continue; }
                foreach (var item in v.EnumerateArray())
                {
                    if (seg.FilterField is not null && !PathSegment.Matches(item, seg.FilterField, seg.FilterValue!)) continue;
                    if (step == lastWildcard && lastArrayFilter is not null && !lastArrayFilter(item)) continue;
                    if (next.Count >= DiagLimits.MaxNodesPerPath) { truncated = true; break; }
                    next.Add(item);
                }
            }
            current = next;
            if (current.Count == 0) break;
        }
        return new PathValues(current, absent, truncated);
    }
}

public readonly record struct PathSegment(string Name, bool Wildcard, string? FilterField = null, string? FilterValue = null)
{
    /// <summary>Whether an array element has <paramref name="field"/> equal to the literal: strings ignore case, numbers compare numerically (invariant), booleans as true/false.</summary>
    public static bool Matches(JsonElement item, string field, string literal)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(field, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.String => string.Equals(v.GetString(), literal, StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Number => double.TryParse(literal, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var want) && v.TryGetDouble(out var got) && got == want,
            JsonValueKind.True => string.Equals(literal, "true", StringComparison.OrdinalIgnoreCase),
            JsonValueKind.False => string.Equals(literal, "false", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }
}

/// <param name="Absent">How many steps found nothing where something was expected. Zero values with zero absent means an existing, empty array.</param>
public sealed record PathValues(IReadOnlyList<JsonElement> Values, int Absent, bool Truncated)
{
    public bool Missing => Values.Count == 0 && Absent > 0;
}
