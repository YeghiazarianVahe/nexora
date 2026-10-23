using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexora.Application.Diagnostics;

/// <summary>
/// The stored and returned shape of a diagnosis. Versioned (<see cref="Schema.Version"/>) and free of any presentation text produced by a model,
/// so an on-prem LLM summary can later be added next to it without changing what the rules decided.
/// </summary>
public sealed record DiagnosisResult(
    int SchemaVersion, string ProfileId, int ProfileVersion, bool Complete,
    IReadOnlyList<Finding> Findings, IReadOnlyList<CollectorStatus> Collectors, IReadOnlyList<string> NotEvaluated)
{
    public static class Schema { public const int Version = 1; }
}

/// <param name="Confidence">high, medium or low.</param>
/// <param name="Severity">info, warning or critical.</param>
/// <param name="Score">Confidence weight x severity weight (1-9); findings are sorted by it.</param>
public sealed record Finding(
    string Id, string Cause, string Confidence, string Severity, int Score,
    IReadOnlyList<EvidenceItem> Evidence, IReadOnlyList<RecommendedAction> Actions);

public sealed record EvidenceItem(string Label, string Value);

/// <summary>
/// A suggestion, never an instruction to the server: a catalog action is started by the operator through the normal job flow (confirmation, role
/// and protected-host checks); a manual step is plain text.
/// </summary>
public sealed record RecommendedAction(
    string Kind, string Label,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ActionCode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<string, JsonElement>? Params = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Instructions = null);

/// <param name="Status">ok, incomplete, failed or missing.</param>
public sealed record CollectorStatus(
    string Id, string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null);

/// <summary>
/// What one collector delivered. The rules only ever read <see cref="Data"/> when <see cref="Status"/> is ok; an incomplete collector's partial
/// output is kept only as evidence for the operator.
/// </summary>
public sealed record CollectorResult(string Id, string Status, string? Reason = null, JsonElement? Data = null)
{
    public static CollectorResult Ok(string id, JsonElement data) => new(id, CollectorStates.Ok, null, data);
    public static CollectorResult Incomplete(string id, string? reason, JsonElement? partial = null) => new(id, CollectorStates.Incomplete, reason, partial);
    public static CollectorResult Failed(string id, string? reason) => new(id, CollectorStates.Failed, reason);
    public static CollectorResult Missing(string id) => new(id, CollectorStates.Missing);
}

public static class CollectorStates
{
    public const string Ok = "ok", Incomplete = "incomplete", Failed = "failed", Missing = "missing";
}

public static class Severities
{
    public const string Info = "info", Warning = "warning", Critical = "critical";
    public static readonly IReadOnlyList<string> All = [Info, Warning, Critical];
    public static int Weight(string s) => s switch { Critical => 3, Warning => 2, _ => 1 };
}

public static class Confidences
{
    public const string High = "high", Medium = "medium", Low = "low";
    public static readonly IReadOnlyList<string> All = [High, Medium, Low];
    public static int Weight(string s) => s switch { High => 3, Medium => 2, _ => 1 };
}

public static class ActionKinds
{
    public const string Catalog = "catalog", Manual = "manual";
}

/// <summary>Values that come from a PC are untrusted text: control and bidi characters are removed and the length is capped.</summary>
public static class DiagText
{
    public const int MaxValueLength = 260;

    public static string Clean(string? text, int max = MaxValueLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = JsonSanitizer.Text(text);
        var sb = new StringBuilder(Math.Min(text.Length, max + 1));
        foreach (var c in text)
        {
            if (char.IsControl(c) || IsInvisibleFormat(c)) { if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' '); continue; }
            sb.Append(c);
            if (sb.Length > max) break;
        }
        var s = sb.ToString().Trim();
        return s.Length <= max ? s : s[..(max - 1)] + "\u2026";
    }

    /// <summary>Zero-width and bidirectional override characters can make a path or name read as something else.</summary>
    private static bool IsInvisibleFormat(char c) =>
        c is '\u200B' or '\u200C' or '\u200D' or '\u200E' or '\u200F' or '\uFEFF' or '\u2028' or '\u2029'
        || (c >= '\u202A' && c <= '\u202E') || (c >= '\u2066' && c <= '\u2069');
}

/// <summary>
/// Collector output comes from a PC and is stored as jsonb, which refuses U+0000 and unpaired surrogates. This removes or replaces them in the JSON
/// text (before parsing, because reading such an escaped string throws) and again in the parsed value, in property names and strings alike, so one
/// odd file name can never make a whole run fail to save.
/// </summary>
public static class JsonSanitizer
{
    /// <summary>Cleans JSON text: a NUL escape is dropped, an unpaired surrogate escape becomes U+FFFD; escaped backslashes are left alone.</summary>
    public static string CleanJsonText(string json)
    {
        json = Text(json);
        var sb = new StringBuilder(json.Length);
        for (var i = 0; i < json.Length; i++)
        {
            if (json[i] != '\\' || i + 1 >= json.Length) { sb.Append(json[i]); continue; }
            if (json[i + 1] != 'u' || !HexAt(json, i, out var code)) { sb.Append(json[i]).Append(json[i + 1]); i++; continue; }
            if (code == 0) { i += 5; continue; }
            if (code is >= 0xD800 and <= 0xDBFF && HexAt(json, i + 6, out var low) && low is >= 0xDC00 and <= 0xDFFF) { sb.Append(json, i, 12); i += 11; continue; }
            if (code is >= 0xD800 and <= 0xDFFF) { sb.Append("\\uFFFD"); i += 5; continue; }
            sb.Append(json, i, 6);
            i += 5;
        }
        return sb.ToString();
    }

    private static bool HexAt(string s, int at, out int code)
    {
        code = 0;
        return at + 6 <= s.Length && s[at] == '\\' && s[at + 1] == 'u'
            && int.TryParse(s.AsSpan(at + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code);
    }

    public static JsonElement Clean(JsonElement e)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) Write(w, e, 0);
        using var doc = JsonDocument.Parse(ms.ToArray());
        return doc.RootElement.Clone();
    }

    /// <summary>A string without U+0000 and with every unpaired surrogate replaced by U+FFFD.</summary>
    public static string Text(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\0') continue;
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[++i]); continue; }
            sb.Append(char.IsSurrogate(c) ? '\uFFFD' : c);
        }
        return sb.ToString();
    }

    private static string Safe(Func<string?> read)
    {
        try { return Text(read()); }
        catch (InvalidOperationException) { return "\uFFFD"; } // an escape the text pass could not repair
    }

    private static void Write(Utf8JsonWriter w, JsonElement e, int depth)
    {
        if (depth > 32) { w.WriteNullValue(); return; }
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in e.EnumerateObject()) { w.WritePropertyName(Safe(() => p.Name)); Write(w, p.Value, depth + 1); }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var i in e.EnumerateArray()) Write(w, i, depth + 1);
                w.WriteEndArray();
                break;
            case JsonValueKind.String: w.WriteStringValue(Safe(e.GetString)); break;
            case JsonValueKind.Number: w.WriteRawValue(e.GetRawText()); break;
            case JsonValueKind.True: w.WriteBooleanValue(true); break;
            case JsonValueKind.False: w.WriteBooleanValue(false); break;
            default: w.WriteNullValue(); break;
        }
    }
}
