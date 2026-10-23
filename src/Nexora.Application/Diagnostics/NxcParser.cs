using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexora.Application.Diagnostics;

/// <summary>
/// Reads the helper's collector output: one line per collector, <c>##NXC &lt;id&gt; &lt;json&gt;</c>. The lines come from a PC, so nothing on them is trusted:
/// an id the profile did not ask for is ignored (a collector cannot speak for another), a repeated id makes that collector incomplete,
/// and a line that is oversize, not JSON or not an object makes its collector incomplete instead of failing the run.
/// </summary>
public static partial class NxcParser
{
    public const string Tag = "##NXC";
    public const int MaxLineLength = 8_000, MaxDepth = 16;

    [GeneratedRegex(@"^[a-z0-9][a-z0-9\-]{0,63}\z")]
    private static partial Regex IdPattern();

    public sealed record Parsed(IReadOnlyList<CollectorResult> Results, int Ignored);

    public static Parsed Parse(IEnumerable<string> lines, IReadOnlyCollection<string> expectedIds)
    {
        var expected = new HashSet<string>(expectedIds, StringComparer.Ordinal);
        var results = new Dictionary<string, CollectorResult>(StringComparer.Ordinal);
        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        var ignored = 0;

        foreach (var raw in lines)
        {
            if (raw is null || !raw.StartsWith(Tag + " ", StringComparison.Ordinal)) continue; // progress, noise and anything else
            var rest = raw.AsSpan(Tag.Length + 1);
            var space = rest.IndexOf(' ');
            var id = (space < 0 ? rest : rest[..space]).ToString();
            if (!IdPattern().IsMatch(id) || !expected.Contains(id)) { ignored++; continue; }
            if (results.ContainsKey(id)) { duplicates.Add(id); continue; }

            results[id] = space < 0 ? CollectorResult.Incomplete(id, "no output")
                : raw.Length > MaxLineLength ? CollectorResult.Incomplete(id, "output too large")
                : ParseOne(id, rest[(space + 1)..].ToString());
        }
        foreach (var id in duplicates) results[id] = CollectorResult.Incomplete(id, "duplicate output");
        return new Parsed(results.Values.ToList(), ignored);
    }

    private static CollectorResult ParseOne(string id, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(JsonSanitizer.CleanJsonText(json), new JsonDocumentOptions { MaxDepth = MaxDepth });
            var root = JsonSanitizer.Clean(doc.RootElement);
            if (root.ValueKind != JsonValueKind.Object) return CollectorResult.Incomplete(id, "invalid output");
            if (Flag(root, "incomplete")) return CollectorResult.Incomplete(id, Reason(root), root);
            if (Flag(root, "failed")) return CollectorResult.Failed(id, Reason(root));
            return CollectorResult.Ok(id, root);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return CollectorResult.Incomplete(id, "invalid output"); }
    }

    private static bool Flag(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? Reason(JsonElement o) =>
        (o.TryGetProperty("incompleteReason", out var r) || o.TryGetProperty("reason", out r)) && r.ValueKind == JsonValueKind.String ? DiagText.Clean(r.GetString(), 120) : null;
}
