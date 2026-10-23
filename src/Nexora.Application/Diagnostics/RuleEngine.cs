using System.Globalization;
using System.Text.Json;

namespace Nexora.Application.Diagnostics;

public interface IRuleEngine
{
    /// <summary>Applies the profile's rules to what the collectors delivered. Deterministic, side-effect free, never throws for bad data.</summary>
    DiagnosisResult Evaluate(DiagProfile profile, IReadOnlyCollection<CollectorResult> results);
}

public sealed class RuleEngine : IRuleEngine
{
    public DiagnosisResult Evaluate(DiagProfile profile, IReadOnlyCollection<CollectorResult> results)
    {
        // One result per id; a repeated id keeps the first (the parser already marks a duplicate as incomplete).
        var byId = new Dictionary<string, CollectorResult>(StringComparer.Ordinal);
        foreach (var r in results ?? [])
            if (r is not null && !string.IsNullOrEmpty(r.Id)) byId.TryAdd(r.Id, r);

        var statuses = profile.Collectors.Select(c =>
        {
            var r = byId.GetValueOrDefault(c.Id);
            return r is null ? new CollectorStatus(c.Id, CollectorStates.Missing) : new CollectorStatus(c.Id, r.Status, r.Reason is null ? null : DiagText.Clean(r.Reason, 120));
        }).ToList();
        var required = profile.Collectors.Where(c => c.Required).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

        bool Usable(string source) => byId.TryGetValue(source, out var r) && r.Status == CollectorStates.Ok && r.Data is not null;
        ConditionEvaluator.SourceLookup lookup = source => Usable(source) ? byId[source].Data : null;

        var findings = new List<Finding>();
        var notEvaluated = new List<string>();
        foreach (var rule in profile.Rules)
        {
            try
            {
                var sources = ConditionEvaluator.Sources(rule.When).Distinct(StringComparer.Ordinal).ToList();
                if (sources.Any(s => required.Contains(s) && !Usable(s))) { notEvaluated.Add(rule.Id); continue; }
                // "needs": data the rule cannot be judged without (for example at least one probed host). Empty or absent is insufficient data, not a "no".
                if (rule.Needs is { Count: > 0 } needs && needs.Any(n => lookup(n.Source) is not { } root
                    || !n.Resolve(root).Values.Any(v => v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)))) { notEvaluated.Add(rule.Id); continue; }
                if (ConditionEvaluator.Evaluate(rule.When, lookup) != Tri.True) continue;
                findings.Add(Build(rule, lookup));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                notEvaluated.Add(rule.Id); // a rule that cannot be evaluated is reported as such, never as a crash of the run
            }
        }

        var sorted = findings.OrderByDescending(f => f.Score).ThenBy(f => f.Id, StringComparer.Ordinal).ToList();
        var complete = statuses.All(s => s.Status == CollectorStates.Ok) && notEvaluated.Count == 0;
        return new DiagnosisResult(DiagnosisResult.Schema.Version, profile.Id, profile.Version, complete, sorted, statuses, notEvaluated);
    }

    private static Finding Build(DiagRule rule, ConditionEvaluator.SourceLookup lookup)
    {
        var evidence = new List<EvidenceItem>();
        foreach (var spec in rule.Evidence)
        {
            if (lookup(spec.Path.Source) is not { } root) continue;
            Func<JsonElement, bool>? filter = null;
            if (spec.Join is { } join && (filter = ConditionEvaluator.JoinFilter(join, lookup)) is null) continue;
            var text = Describe(spec, spec.Path.Resolve(root, filter));
            if (text.Length > 0) evidence.Add(new EvidenceItem(DiagText.Clean(spec.Label, 80), text));
        }
        var actions = rule.Actions.Select(a => new RecommendedAction(
            a.Kind, DiagText.Clean(a.Label, 120), a.ActionCode,
            a.Params is null ? null : new Dictionary<string, JsonElement>(a.Params),
            a.Instructions)).ToList();
        return new Finding(rule.Id, DiagText.Clean(rule.Cause, 300), rule.Confidence, rule.Severity,
            Confidences.Weight(rule.Confidence) * Severities.Weight(rule.Severity), evidence, actions);
    }

    /// <summary>The values of one evidence path as a single sanitised, size-capped string.</summary>
    private static string Describe(EvidenceSpec spec, PathValues found)
    {
        var parts = new List<string>();
        foreach (var v in found.Values)
        {
            if (parts.Count >= DiagLimits.MaxEvidenceValues) { parts.Add("…"); break; }
            var s = Format(spec.Format, v);
            if (s.Length > 0 && !parts.Contains(s, StringComparer.Ordinal)) parts.Add(s);
        }
        return DiagText.Clean(string.Join("; ", parts));
    }

    private static string Format(string format, JsonElement v)
    {
        switch (format)
        {
            case "count":
                return v.ValueKind == JsonValueKind.Array ? v.GetArrayLength().ToString(CultureInfo.InvariantCulture) : "";
            case "bytes":
                return ConditionEvaluator.TryNumber(v, out var b) ? Bytes(b) : "";
            case "percent":
                return ConditionEvaluator.TryNumber(v, out var p) ? p.ToString("0.#", CultureInfo.InvariantCulture) + " %" : "";
            case "number":
                return ConditionEvaluator.TryNumber(v, out var n) ? n.ToString("0.##", CultureInfo.InvariantCulture) : "";
            default:
                return v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString() ?? "",
                    JsonValueKind.Number => v.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => "", // objects and arrays are never dumped into evidence
                };
        }
    }

    public static string Bytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var i = 0;
        var v = Math.Abs(bytes);
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return (bytes < 0 ? "-" : "") + v.ToString(i == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[i];
    }
}
