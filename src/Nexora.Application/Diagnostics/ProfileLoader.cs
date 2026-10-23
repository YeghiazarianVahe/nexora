using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nexora.Application.Jobs;

namespace Nexora.Application.Diagnostics;

/// <summary>Thrown at startup when a shipped profile (or collector) is invalid. The message lists every problem found.</summary>
public sealed class DiagnosticProfileException(IReadOnlyList<string> errors)
    : Exception("Diagnostic profiles are invalid: " + string.Join(" | ", errors.Take(25)) + (errors.Count > 25 ? $" | ... and {errors.Count - 25} more" : ""))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>The profiles this build ships, validated once at startup.</summary>
public interface IDiagnosticProfiles
{
    IReadOnlyList<DiagProfile> All { get; }
    DiagProfile? Find(string? id);
}

public sealed class ProfileCatalog : IDiagnosticProfiles
{
    public ProfileCatalog(IReadOnlyList<DiagProfile> profiles) { All = profiles; }
    public IReadOnlyList<DiagProfile> All { get; }
    public DiagProfile? Find(string? id) => id is null ? null : All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    /// <summary>Loads the embedded <c>Diagnostics/Profiles/*.json</c> and validates them against the collectors and the action catalog. Throws <see cref="DiagnosticProfileException"/>.</summary>
    public static ProfileCatalog FromAssembly(Assembly assembly, IReadOnlyCollection<string> knownCollectorIds) =>
        new(ProfileLoader.Load(EmbeddedFiles.Read(assembly, "Diagnostics/Profiles/", ".json"), knownCollectorIds));
}

/// <summary>Reads embedded resources whose logical name starts with a prefix (set by LogicalName in the project file).</summary>
public static class EmbeddedFiles
{
    public static IReadOnlyList<(string Name, string Text)> Read(Assembly assembly, string prefix, string extension)
    {
        var files = new List<(string, string)>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(extension, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            files.Add((resource[prefix.Length..], reader.ReadToEnd()));
        }
        return files;
    }
}

/// <summary>
/// Parses and validates profile files. The language is closed: unknown properties, operators, collectors, catalog actions or bad paths and regular
/// expressions are errors, so a typo fails the server's start instead of silently disabling a rule.
/// </summary>
public static partial class ProfileLoader
{
    [GeneratedRegex(@"^[a-z0-9][a-z0-9\-]{0,63}\z")] private static partial Regex ProfileIdPattern();
    [GeneratedRegex(@"^[a-z0-9][a-z0-9\-.]{0,63}\z")] private static partial Regex RuleIdPattern();

    private static readonly string[] Formats = ["text", "bytes", "percent", "count", "number"];

    public static IReadOnlyList<DiagProfile> Load(IEnumerable<(string Name, string Json)> files, IReadOnlyCollection<string> knownCollectorIds)
    {
        var errors = new List<string>();
        var profiles = new List<DiagProfile>();
        foreach (var (name, json) in files)
        {
            var profile = Parse(name, json, knownCollectorIds, errors);
            if (profile is null) continue;
            if (profiles.Any(p => p.Id == profile.Id)) errors.Add($"{name}: profile id '{profile.Id}' is used by another file.");
            else profiles.Add(profile);
        }
        if (errors.Count > 0) throw new DiagnosticProfileException(errors);
        return profiles;
    }

    public static DiagProfile? Parse(string file, string json, IReadOnlyCollection<string> knownCollectorIds, List<string> errors)
    {
        var start = errors.Count;
        void Err(string msg) => errors.Add($"{file}: {msg}");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException ex) { Err("not valid JSON (" + ex.Message.Split('.')[0] + ")."); return null; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { Err("the profile must be a JSON object."); return null; }
            Unknown(root, ["id", "name", "description", "version", "collectors", "rules"], Err, "profile");

            var id = Str(root, "id");
            if (id is null || !ProfileIdPattern().IsMatch(id)) { Err("'id' is missing or not a valid profile id (lowercase letters, digits, hyphen)."); id ??= "?"; }
            var pname = Str(root, "name");
            if (string.IsNullOrWhiteSpace(pname) || pname.Length > 100) Err("'name' is missing or longer than 100 characters.");
            var description = Str(root, "description") ?? "";
            if (description.Length > 300) Err("'description' is longer than 300 characters.");
            var version = 1;
            if (root.TryGetProperty("version", out var v) && (!v.TryGetInt32(out version) || version < 1)) Err("'version' must be a whole number from 1.");

            var collectors = ParseCollectors(root, knownCollectorIds, Err);
            var allowed = collectors.Select(c => c.Id).Concat(DiagPath.ServerSources).ToHashSet(StringComparer.Ordinal);

            var rules = new List<DiagRule>();
            if (!root.TryGetProperty("rules", out var rulesEl) || rulesEl.ValueKind != JsonValueKind.Array) Err("'rules' must be an array.");
            else
            {
                if (rulesEl.GetArrayLength() > DiagLimits.MaxRules) Err($"more than {DiagLimits.MaxRules} rules.");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var r in rulesEl.EnumerateArray().Take(DiagLimits.MaxRules))
                {
                    var rule = ParseRule(r, allowed, Err);
                    if (rule is null) continue;
                    if (!seen.Add(rule.Id)) Err($"duplicate rule id '{rule.Id}'.");
                    else rules.Add(rule);
                }
            }
            return errors.Count > start ? null : new DiagProfile(id, pname!.Trim(), description.Trim(), version, collectors, rules);
        }
    }

    private static List<DiagCollectorRef> ParseCollectors(JsonElement root, IReadOnlyCollection<string> known, Action<string> err)
    {
        var list = new List<DiagCollectorRef>();
        if (!root.TryGetProperty("collectors", out var el) || el.ValueKind != JsonValueKind.Array) { err("'collectors' must be an array."); return list; }
        if (el.GetArrayLength() > DiagLimits.MaxCollectors) err($"more than {DiagLimits.MaxCollectors} collectors.");
        foreach (var c in el.EnumerateArray().Take(DiagLimits.MaxCollectors))
        {
            if (c.ValueKind != JsonValueKind.Object) { err("a collector entry must be an object."); continue; }
            Unknown(c, ["id", "required", "timeoutSeconds"], err, "collector");
            var cid = Str(c, "id");
            if (cid is null) { err("a collector has no 'id'."); continue; }
            if (!known.Contains(cid, StringComparer.Ordinal)) err($"unknown collector id '{cid}'.");
            if (list.Any(x => x.Id == cid)) err($"collector '{cid}' is listed twice.");
            var required = c.TryGetProperty("required", out var rq) && rq.ValueKind == JsonValueKind.True;
            if (c.TryGetProperty("required", out var rq2) && rq2.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) err($"collector '{cid}': 'required' must be true or false.");
            var timeout = 30;
            if (c.TryGetProperty("timeoutSeconds", out var t) && (!t.TryGetInt32(out timeout) || timeout is < 1 or > 120)) err($"collector '{cid}': 'timeoutSeconds' must be 1-120.");
            list.Add(new DiagCollectorRef(cid, required, timeout));
        }
        return list;
    }

    private static DiagRule? ParseRule(JsonElement r, HashSet<string> sources, Action<string> err)
    {
        if (r.ValueKind != JsonValueKind.Object) { err("a rule must be an object."); return null; }
        var errs = new List<string>();
        void E(string m) { errs.Add(m); }
        Unknown(r, ["id", "cause", "severity", "confidence", "when", "evidence", "actions", "needs"], E, "rule");
        var id = Str(r, "id");
        if (id is null || !RuleIdPattern().IsMatch(id)) { err("a rule has a missing or invalid 'id'."); return null; }
        string P(string m) => $"rule '{id}': {m}";

        var cause = Str(r, "cause");
        if (string.IsNullOrWhiteSpace(cause) || cause.Length > 300) E("'cause' is missing or longer than 300 characters.");
        var severity = Str(r, "severity");
        if (severity is null || !Severities.All.Contains(severity)) E("'severity' must be info, warning or critical.");
        var confidence = Str(r, "confidence");
        if (confidence is null || !Confidences.All.Contains(confidence)) E("'confidence' must be high, medium or low.");

        Condition? when = null;
        if (!r.TryGetProperty("when", out var w)) E("'when' is missing.");
        else
        {
            var nodes = 0;
            when = ParseCondition(w, 1, ref nodes, sources, E);
        }

        var evidence = new List<EvidenceSpec>();
        if (r.TryGetProperty("evidence", out var ev))
        {
            if (ev.ValueKind != JsonValueKind.Array) E("'evidence' must be an array.");
            else
            {
                if (ev.GetArrayLength() > DiagLimits.MaxEvidencePerRule) E($"more than {DiagLimits.MaxEvidencePerRule} evidence items.");
                foreach (var item in ev.EnumerateArray().Take(DiagLimits.MaxEvidencePerRule))
                {
                    Unknown(item, ["label", "path", "format", "join"], E, "evidence");
                    var label = Str(item, "label");
                    var pathText = Str(item, "path");
                    var format = Str(item, "format") ?? "text";
                    if (string.IsNullOrWhiteSpace(label) || label.Length > 80) { E("an evidence item needs a 'label' of up to 80 characters."); continue; }
                    if (!Formats.Contains(format)) { E($"evidence '{label}': unknown format '{format}'."); continue; }
                    if (!DiagPath.TryParse(pathText, out var path, out var pe)) { E($"evidence '{label}': bad path ({pe})."); continue; }
                    if (!sources.Contains(path.Source)) { E($"evidence '{label}': '{path.Source}' is not a collector of this profile."); continue; }
                    JoinSpec? ej = null;
                    if (item.TryGetProperty("join", out var ejEl) && (ej = ParseJoin(ejEl, path, sources, m => E($"evidence '{label}': {m}"))) is null) continue;
                    evidence.Add(new EvidenceSpec(label.Trim(), path, format, ej));
                }
            }
        }

        var actions = new List<ActionSpec>();
        if (r.TryGetProperty("actions", out var ac))
        {
            if (ac.ValueKind != JsonValueKind.Array) E("'actions' must be an array.");
            else
            {
                if (ac.GetArrayLength() > DiagLimits.MaxActionsPerRule) E($"more than {DiagLimits.MaxActionsPerRule} actions.");
                foreach (var a in ac.EnumerateArray().Take(DiagLimits.MaxActionsPerRule))
                    if (ParseAction(a, E) is { } spec) actions.Add(spec);
            }
        }

        var needs = new List<DiagPath>();
        if (r.TryGetProperty("needs", out var nd))
        {
            if (nd.ValueKind != JsonValueKind.Array) E("'needs' must be an array of paths.");
            else
                foreach (var n in nd.EnumerateArray().Take(5))
                {
                    if (!DiagPath.TryParse(n.ValueKind == JsonValueKind.String ? n.GetString() : null, out var np, out var ne)) E($"bad 'needs' path ({ne}).");
                    else if (!sources.Contains(np.Source)) E($"'needs' path '{np.Text}': '{np.Source}' is not a collector of this profile.");
                    else needs.Add(np);
                }
        }
        foreach (var e in errs) err(P(e));
        if (errs.Count > 0 || when is null) return null;
        return new DiagRule(id, cause!.Trim(), severity!, confidence!, when, evidence, actions, needs);
    }

    private static ActionSpec? ParseAction(JsonElement a, Action<string> err)
    {
        if (a.ValueKind != JsonValueKind.Object) { err("an action must be an object."); return null; }
        var kind = Str(a, "kind");
        var label = Str(a, "label");
        if (string.IsNullOrWhiteSpace(label) || label.Length > 120) { err("an action needs a 'label' of up to 120 characters."); return null; }
        switch (kind)
        {
            case ActionKinds.Catalog:
            {
                Unknown(a, ["kind", "actionCode", "params", "label"], err, "action");
                var code = Str(a, "actionCode");
                var def = ActionCatalog.Find(code);
                if (def is null || def.Demo) { err($"action '{label}': unknown catalog action code '{code}'."); return null; }
                if (!def.Implemented) { err($"action '{label}': catalog action '{code}' is not implemented, so it cannot be recommended."); return null; }
                Dictionary<string, JsonElement>? values = null;
                if (a.TryGetProperty("params", out var ps))
                {
                    if (ps.ValueKind != JsonValueKind.Object) { err($"action '{label}': 'params' must be an object."); return null; }
                    values = ps.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
                }
                var (_, problem) = ParameterValidator.Validate(def, values is null ? null : new Dictionary<string, JsonElement>(values));
                if (problem is not null) { err($"action '{label}': {problem}"); return null; }
                return new ActionSpec(ActionKinds.Catalog, label.Trim(), code, values, null);
            }
            case ActionKinds.Manual:
            {
                Unknown(a, ["kind", "label", "instructions"], err, "action");
                var text = Str(a, "instructions");
                if (string.IsNullOrWhiteSpace(text) || text.Length > 1_000) { err($"action '{label}': 'instructions' are missing or longer than 1000 characters."); return null; }
                return new ActionSpec(ActionKinds.Manual, label.Trim(), null, null, text.Trim());
            }
            default:
                err($"action '{label}': kind must be 'catalog' or 'manual'.");
                return null;
        }
    }

    private static Condition? ParseCondition(JsonElement e, int depth, ref int nodes, HashSet<string> sources, Action<string> err)
    {
        if (++nodes > DiagLimits.MaxConditionNodes) { err($"the condition has more than {DiagLimits.MaxConditionNodes} parts."); return null; }
        if (depth > DiagLimits.MaxConditionDepth) { err($"the condition is nested deeper than {DiagLimits.MaxConditionDepth} levels."); return null; }
        if (e.ValueKind != JsonValueKind.Object) { err("a condition must be an object."); return null; }

        var keys = e.EnumerateObject().Select(p => p.Name).ToList();
        if (keys.Contains("all") || keys.Contains("any"))
        {
            var name = keys.Contains("all") ? "all" : "any";
            if (keys.Count != 1) { err($"'{name}' cannot be combined with other keys."); return null; }
            var arr = e.GetProperty(name);
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() is 0 or > 20) { err($"'{name}' must be a list of 1-20 conditions."); return null; }
            var items = new List<Condition>();
            foreach (var item in arr.EnumerateArray())
            {
                var c = ParseCondition(item, depth + 1, ref nodes, sources, err);
                if (c is null) return null;
                items.Add(c);
            }
            return name == "all" ? new AllOf(items) : new AnyOf(items);
        }
        if (keys.Contains("not"))
        {
            if (keys.Count != 1) { err("'not' cannot be combined with other keys."); return null; }
            var inner = ParseCondition(e.GetProperty("not"), depth + 1, ref nodes, sources, err);
            return inner is null ? null : new NotOf(inner);
        }

        Unknown(e, ["path", "op", "value", "join"], err, "condition");
        var pathText = Str(e, "path");
        var op = Str(e, "op");
        if (op is null || !DiagOps.All.Contains(op)) { err($"unknown operator '{op}'."); return null; }
        if (!DiagPath.TryParse(pathText, out var path, out var pe)) { err($"bad path '{pathText}' ({pe})."); return null; }
        if (!sources.Contains(path.Source)) { err($"path '{path.Text}': '{path.Source}' is not a collector of this profile."); return null; }

        JsonElement? literal = e.TryGetProperty("value", out var val) ? val.Clone() : null;
        Regex? pattern = null;
        switch (op)
        {
            case DiagOps.Gt or DiagOps.Gte or DiagOps.Lt or DiagOps.Lte or DiagOps.CountGt:
                if (literal is not { ValueKind: JsonValueKind.Number } num || !num.TryGetDouble(out var d) || !double.IsFinite(d)) { err($"'{op}' on '{path.Text}' needs a numeric value."); return null; }
                break;
            case DiagOps.Eq or DiagOps.Ne or DiagOps.Contains:
                if (literal is not { ValueKind: JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False }) { err($"'{op}' on '{path.Text}' needs a string, number or true/false value."); return null; }
                break;
            case DiagOps.In:
                if (literal is not { ValueKind: JsonValueKind.Array } list || list.GetArrayLength() is 0 or > DiagLimits.MaxInList
                    || list.EnumerateArray().Any(x => x.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)))
                { err($"'in' on '{path.Text}' needs a list of 1-{DiagLimits.MaxInList} strings, numbers or booleans."); return null; }
                break;
            case DiagOps.Exists:
                if (literal is not null && literal.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { err($"'exists' on '{path.Text}' takes true or false (or nothing)."); return null; }
                break;
            case DiagOps.AnyMatch:
                if (literal is not { ValueKind: JsonValueKind.String } pat || string.IsNullOrEmpty(pat.GetString()) || pat.GetString()!.Length > DiagLimits.MaxPatternLength)
                { err($"'anyMatch' on '{path.Text}' needs a pattern of up to {DiagLimits.MaxPatternLength} characters."); return null; }
                try { pattern = new Regex(pat.GetString()!, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, DiagLimits.RegexTimeout); }
                catch (ArgumentException) { err($"'anyMatch' on '{path.Text}': the pattern is not a valid regular expression."); return null; }
                break;
        }
        JoinSpec? join = null;
        if (e.TryGetProperty("join", out var joinEl) && (join = ParseJoin(joinEl, path, sources, err)) is null) return null;
        return new Leaf(path, op, literal, pattern, join);
    }

    private static JoinSpec? ParseJoin(JsonElement j, DiagPath path, HashSet<string> sources, Action<string> err)
    {
        if (j.ValueKind != JsonValueKind.Object) { err($"'join' on '{path.Text}' must be an object."); return null; }
        Unknown(j, ["key", "with"], err, "join");
        var key = Str(j, "key");
        if (key is null || !JoinKeyPattern().IsMatch(key)) { err($"'join' on '{path.Text}' needs a 'key' (a property name)."); return null; }
        if (!path.Segments.Any(s => s.Wildcard)) { err($"'join' on '{path.Text}' needs a path with an array step ([*])."); return null; }
        if (!DiagPath.TryParse(Str(j, "with"), out var with, out var pe)) { err($"'join' on '{path.Text}': bad 'with' path ({pe})."); return null; }
        if (!sources.Contains(with.Source)) { err($"'join' on '{path.Text}': '{with.Source}' is not a collector of this profile."); return null; }
        return new JoinSpec(key, with);
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,63}\z")] private static partial Regex JoinKeyPattern();

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static void Unknown(JsonElement o, string[] allowed, Action<string> err, string what)
    {
        if (o.ValueKind != JsonValueKind.Object) return;
        foreach (var p in o.EnumerateObject())
            if (!allowed.Contains(p.Name, StringComparer.Ordinal)) err($"unknown property '{p.Name}' in {what}.");
    }
}
