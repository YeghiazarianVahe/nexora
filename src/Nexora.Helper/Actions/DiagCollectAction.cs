using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nexora.Contracts;

namespace Nexora.Helper.Actions;

/// <summary>
/// The collectors this helper build is willing to run. Compiled in on purpose: a signed job can name a collector, but it cannot introduce one.
/// Approved bodies are embedded from the same source files as the server and pinned by hash.
/// </summary>
public static class DiagCollectors
{
    private static readonly IReadOnlyDictionary<string, string> Hashes = Load();
    public static IReadOnlyCollection<string> Ids => Hashes.Keys.ToArray();
    internal static bool Approves(string id, string hash) => Hashes.TryGetValue(id, out var approved)
        && string.Equals(approved, hash, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string> Load()
    {
        var assembly = typeof(DiagCollectors).Assembly;
        const string prefix = "Diagnostics/Collectors/";
        return assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".ps1", StringComparison.Ordinal))
            .ToDictionary(n => Path.GetFileNameWithoutExtension(n), n =>
            {
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reader.ReadToEnd())));
            }, StringComparer.Ordinal);
    }
}

/// <summary>
/// diag.collect: read-only collection of diagnostic facts from ONE target, using the same SYSTEM scheduled-task mechanism as <see cref="ScriptRunAction"/>
/// (job folder over the admin share, one-off task, output file). The collector bodies and their SHA-256 arrive inside the signed job as
/// Parameters["collectors"], a JSON array of {"id","sha256","body","timeoutSeconds"}. The helper (1) refuses the whole job before touching the PC
/// when any id is not in its own allow-list or any body does not match its hash, (2) runs the collectors one after another, each with its own timeout,
/// and (3) writes one line per collector: <c>##NXC &lt;id&gt; &lt;json&gt;</c>. A collector that times out or fails yields a line with
/// <c>"incomplete":true</c>; that is a Warning for the job, not a failure.
/// </summary>
public sealed partial class DiagCollectAction : IJobAction
{
    public const string Tag = "##NXC";
    public const int MaxLineLength = 4000, MaxCollectors = 32, MaxBodyLength = 20_000, DefaultTimeoutSeconds = 30, MaxTimeoutSeconds = 120;

    public const int MaxCaptureLength = 64 * 1024;

    private readonly ISystemTaskHost _host;
    private readonly ScriptRunAction _runner;
    private readonly IReadOnlyCollection<string> _allowed;
    private readonly bool _requirePinnedHash;

    public DiagCollectAction(ISystemTaskHost? host = null, TimeSpan? poll = null) : this(host, poll, DiagCollectors.Ids) { _requirePinnedHash = true; }

    /// <summary>Tests only: the allow-list of a shipped helper is always <see cref="DiagCollectors.Ids"/>.</summary>
    internal DiagCollectAction(ISystemTaskHost? host, TimeSpan? poll, IReadOnlyCollection<string> allowed)
    {
        _host = host ?? new AdminShareSystemTaskHost();
        _runner = new ScriptRunAction(_host, poll);
        _allowed = allowed;
    }

    /// <summary>A job folder / task id of its own for each collector, so no folder or task name is ever reused within or across jobs.</summary>
    internal static Guid RunId(Guid target, int index)
    {
        var hash = SHA256.HashData([.. target.ToByteArray(), .. BitConverter.GetBytes(index)]);
        return new Guid(hash.AsSpan(0, 16));
    }

    public IReadOnlyList<string> Codes { get; } = ["diag.collect"];

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._\-]{0,63}\z")]
    private static partial Regex IdPattern();

    private sealed record CollectorSpec(string? Id, string? Sha256, string? Body, int? TimeoutSeconds);

    /// <summary>Keeps only the stdout lines of one collector; helper progress ("system") lines pass through.</summary>
    private sealed class CaptureSink(IJobSink inner) : IJobSink
    {
        private int _size;
        public List<string> Lines { get; } = [];
        public bool Overflow { get; private set; }
        public Task Log(string stream, string line)
        {
            if (stream == "stdout")
            {
                _size += line.Length;
                if (_size > MaxCaptureLength) Overflow = true; else Lines.Add(line);
                return Task.CompletedTask;
            }
            return inner.Log(stream, line);
        }
        public Task Progress(int percent) => Task.CompletedTask;
    }

    public async Task<ActionOutcome> RunAsync(JobPayload job, IJobSink sink, CancellationToken ct)
    {
        if (!HelperInput.Host(job.Host)) return Outcomes.Fail("Invalid target host.", 2);

        var specs = Parse(job.Parameters.GetValueOrDefault("collectors"), out var parseError);
        if (specs is null) return Outcomes.Fail(parseError!, 2);

        // Validate everything first: nothing is written to the PC unless the whole job is acceptable.
        foreach (var s in specs)
        {
            if (s.Id is null || !IdPattern().IsMatch(s.Id)) return Outcomes.Fail("Refused: a collector id is invalid.", 2);
            if (!_allowed.Contains(s.Id, StringComparer.Ordinal)) return Outcomes.Fail($"Refused: collector '{s.Id}' is not known to this helper.", 2);
            if (string.IsNullOrEmpty(s.Body) || string.IsNullOrEmpty(s.Sha256)) return Outcomes.Fail($"Refused: collector '{s.Id}' carries no script.", 2);
            if (s.Body.Length > MaxBodyLength) return Outcomes.Fail($"Refused: collector '{s.Id}' is larger than the allowed size.", 2);
            var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.Body)));
            if (_requirePinnedHash && !DiagCollectors.Approves(s.Id, actual)) return Outcomes.Fail($"Refused: collector '{s.Id}' differs from the script approved in this helper build.", 2);
            if (!string.Equals(actual, s.Sha256, StringComparison.OrdinalIgnoreCase)) return Outcomes.Fail($"Refused: collector '{s.Id}' does not match its approved hash.", 2);
        }

        var incomplete = 0;
        for (var i = 0; i < specs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var s = specs[i];
            var timeout = Math.Clamp(s.TimeoutSeconds ?? DefaultTimeoutSeconds, 1, MaxTimeoutSeconds);
            await sink.Log("system", $"[helper] collector {s.Id} ({(i + 1).ToString(CultureInfo.InvariantCulture)} of {specs.Count.ToString(CultureInfo.InvariantCulture)}), limit {timeout.ToString(CultureInfo.InvariantCulture)} s");

            var capture = new CaptureSink(sink);
            var run = job with { TargetId = RunId(job.TargetId, i), TimeoutSeconds = timeout };
            string json;
            if (await ExistsAsync(job.Host, run.TargetId.ToString("D"), ct))
            {
                await sink.Log("system", $"[helper] collector {s.Id}: its job folder already exists, not running it");
                incomplete++;
                await sink.Log("stdout", Line(s.Id!, Incomplete("job folder exists")));
                continue;
            }
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(timeout));
            try
            {
                var outcome = await _runner.RunBodyAsync(run, s.Body!, capture, limit.Token, null);
                json = capture.Overflow ? Incomplete("too large")
                    : outcome.State == "Succeeded" ? ToJson(capture.Lines) : Incomplete($"exit {(outcome.ExitCode ?? 1).ToString(CultureInfo.InvariantCulture)}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                json = Incomplete("timeout"); // this collector's own limit; the job carries on with the next one
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await sink.Log("system", $"[helper] collector {s.Id} could not run: {ex.GetType().Name}");
                json = Incomplete("error");
            }
            var line = Line(s.Id!, json);
            if (line.Contains("\"incomplete\":true", StringComparison.Ordinal)) incomplete++;
            await sink.Log("stdout", line);
            await sink.Progress((i + 1) * 100 / specs.Count);
        }

        return incomplete == 0
            ? Outcomes.Ok($"Collected {specs.Count.ToString(CultureInfo.InvariantCulture)} item(s).")
            : new ActionOutcome("Warning", 0, $"Collected {(specs.Count - incomplete).ToString(CultureInfo.InvariantCulture)} of {specs.Count.ToString(CultureInfo.InvariantCulture)} item(s); {incomplete.ToString(CultureInfo.InvariantCulture)} incomplete.");
    }

    /// <summary>The tagged line for one collector, never longer than <see cref="MaxLineLength"/>; a larger result becomes an incomplete line.</summary>
    internal static string Line(string id, string json)
    {
        var line = $"{Tag} {id} {json}";
        return line.Length <= MaxLineLength ? line : $"{Tag} {id} {Incomplete("too large")}"; // never cut JSON in half
    }

    /// <summary>True when anything is already at this run's job folder (best effort; the folder is also created with a restrictive ACL).</summary>
    private async Task<bool> ExistsAsync(string host, string runId, CancellationToken ct)
    {
        foreach (var file in new[] { ScriptRunAction.ScriptFile, ScriptRunAction.RunnerFile, ScriptRunAction.OutputFile, ScriptRunAction.ExitFile })
            if (await _host.ReadAsync(host, runId, file, 0, ct) is not null) return true;
        return false;
    }

    internal static string Incomplete(string reason) => JsonSerializer.Serialize(new { incomplete = true, reason });

    /// <summary>The collector's output as one JSON value on one line; anything that is not JSON is reported as incomplete rather than passed on.</summary>
    private static string ToJson(List<string> lines)
    {
        var text = string.Join(' ', lines.Select(l => l.Trim()).Where(l => l.Length > 0));
        if (text.Length == 0) return Incomplete("no output");
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.GetRawText().ReplaceLineEndings(" ");
        }
        catch (JsonException) { return Incomplete("invalid output"); }
    }

    private static List<CollectorSpec>? Parse(string? raw, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(raw)) { error = "The job carries no collectors."; return null; }
        try
        {
            var list = JsonSerializer.Deserialize<List<CollectorSpec>>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (list is null || list.Count == 0) { error = "The job carries no collectors."; return null; }
            if (list.Count > MaxCollectors) { error = "The job carries too many collectors."; return null; }
            if (list.Any(s => s is null)) { error = "The collector list is malformed."; return null; }
            return list;
        }
        catch (JsonException) { error = "The collector list is malformed."; return null; }
    }
}
