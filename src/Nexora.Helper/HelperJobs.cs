using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR.Client;
using Nexora.Contracts;
using Nexora.Helper.Actions;

namespace Nexora.Helper;

/// <summary>
/// Runs jobs sent by the server (protocol v2) with this operator's own token. A job is accepted only if its MAC verifies against
/// the session key received when this helper connected, it has not expired, it names this operator, and the helper itself knows
/// the action code. Anything else is refused and reported.
/// </summary>
public sealed class HelperJobs(HubConnection connection, string ownSid, byte[]? pinnedServerKey, Action<string> log)
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<Guid, byte> _seen = new();
    private readonly ConcurrentDictionary<string, DateTime> _seenNonces = new();

    public static bool IsKnown(string code) => ActionRegistry.IsKnown(code);

    public void Cancel(Guid targetId)
    {
        if (_running.TryGetValue(targetId, out var cts)) { log($"Cancel requested for target {targetId}"); cts.Cancel(); }
    }

    public async Task HandleAsync(JobEnvelope envelope, CancellationToken appStop)
    {
        var (payload, error) = JobTrust.Verify(envelope, pinnedServerKey, ownSid, DateTime.UtcNow, IsKnown);
        if (payload is null)
        {
            log($"Refused a job: {error}");
            await TryRejectAsync(envelope.Payload, $"Rejected by helper: {Describe(error)}");
            return;
        }
        if (!_seenNonces.TryAdd(payload.Nonce, payload.ExpiresUtc)) { log($"Ignored a replayed job (nonce) for target {payload.TargetId}"); return; }
        foreach (var old in _seenNonces.Where(kv => kv.Value < DateTime.UtcNow.AddMinutes(-5)).Select(kv => kv.Key).ToList()) _seenNonces.TryRemove(old, out _);
        if (!_seen.TryAdd(payload.TargetId, 0)) { log($"Ignored a replayed job for target {payload.TargetId}"); return; }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(appStop);
        _running[payload.TargetId] = cts;
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, payload.TimeoutSeconds)));
        log($"Running {payload.ActionCode} on {payload.Host} (job {payload.JobId})");
        try
        {
            await connection.InvokeAsync<bool>("JobTargetStarted", payload.TargetId, appStop);
            var sink = new Sink(connection, payload.TargetId);
            await Execute(payload, sink, cts.Token);
        }
        catch (OperationCanceledException)
        {
            await Safe(() => connection.InvokeAsync<bool>("JobTargetCompleted", payload.TargetId, "Cancelled", (int?)null, "Cancelled on the helper.", CancellationToken.None));
        }
        catch (Exception ex)
        {
            log($"Job failed: {ex.Message}");
            await Safe(() => connection.InvokeAsync<bool>("JobTargetCompleted", payload.TargetId, "Failed", (int?)null, $"Helper error: {ex.Message}", CancellationToken.None));
        }
        finally
        {
            _running.TryRemove(payload.TargetId, out _);
            _seen.TryRemove(payload.TargetId, out _); // the nonce set still blocks a replay until the job expires; this one must not grow forever
        }
    }

    private static async Task Execute(JobPayload job, Sink sink, CancellationToken ct)
    {
        var action = ActionRegistry.Find(job.ActionCode) ?? throw new InvalidOperationException($"No executor for {job.ActionCode}.");
        var outcome = await action.RunAsync(job, sink, ct);
        await sink.Complete(outcome.State, outcome.ExitCode ?? 0, outcome.Summary);
    }

    /// <summary>
    /// Plain words for the job log. A signature problem is permanent (the helper pins another server key, or none), so it is reported as a
    /// failure with the fix; it is not retried, since the same signed job would be refused again. (The old connection-bound MAC that could
    /// fail after a reconnect no longer exists, which was the retryable case.)
    /// </summary>
    internal static string Describe(JobTrust.Error error) => error switch
    {
        JobTrust.Error.NoPinnedKey => "this helper has no server key pinned. Download the helper again from Nexora.",
        JobTrust.Error.BadSignature => "the job is not signed by the server this helper trusts. If the server key changed, download the helper again from Nexora.",
        JobTrust.Error.Expired => "the job arrived too late (is the clock on this PC correct?). Run it again.",
        JobTrust.Error.WrongOperator => "the job is addressed to another operator.",
        JobTrust.Error.UnknownAction => "this helper does not know that action.",
        _ => error.ToString(),
    };

    private async Task TryRejectAsync(string untrustedPayload, string reason)
    {
        // The payload is untrusted here; the server still verifies that the target belongs to this operator.
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(untrustedPayload);
            if (doc.RootElement.TryGetProperty("targetId", out var id) && id.TryGetGuid(out var target))
                await connection.InvokeAsync<bool>("JobTargetCompleted", target, "Failed", (int?)null, reason);
        }
        catch (Exception) { /* nothing safe to report */ }
    }

    private static async Task Safe(Func<Task> action)
    {
        try { await action(); } catch (Exception) { /* the connection may be gone; the server will time the target out */ }
    }

    private sealed class Sink(HubConnection connection, Guid targetId) : IJobSink
    {
        public Task Log(string stream, string line) => connection.InvokeAsync<bool>("JobLog", targetId, stream, new[] { line });
        public Task Progress(int percent) => connection.InvokeAsync<bool>("JobProgress", targetId, percent);
        public Task Complete(string state, int exitCode, string summary) => connection.InvokeAsync<bool>("JobTargetCompleted", targetId, state, exitCode, summary);
    }
}
