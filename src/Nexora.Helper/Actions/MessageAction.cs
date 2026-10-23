using System.ComponentModel;
using System.Diagnostics;
using Nexora.Contracts;

namespace Nexora.Helper.Actions;

public sealed record MessageCommandResult(int ExitCode, string Output, string Error);

/// <summary>Native Windows session messaging from the operator's own helper. No remote SYSTEM task, admin-share script, or shell.</summary>
public sealed class MessageAction(
    Func<ProcessStartInfo, CancellationToken, Task<MessageCommandResult>>? run = null,
    TimeSpan? timeout = null) : IJobAction
{
    private readonly Func<ProcessStartInfo, CancellationToken, Task<MessageCommandResult>> _run = run ?? RunProcessAsync;
    public IReadOnlyList<string> Codes { get; } = ["session.message"];

    public async Task<ActionOutcome> RunAsync(JobPayload job, IJobSink sink, CancellationToken ct)
    {
        if (job.ActionCode != "session.message") return Outcomes.Fail("Unsupported message action.", 2);
        if (!HelperInput.Host(job.Host)) return Outcomes.Fail("Invalid target host.", 2);
        var message = job.Parameters.GetValueOrDefault("message");
        if (string.IsNullOrWhiteSpace(message) || message.Length > 255 || message.Any(char.IsControl))
            return Outcomes.Fail("The message must be 1-255 characters without control characters.", 2);

        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msg.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add("*");
        start.ArgumentList.Add($"/server:{job.Host}");
        start.ArgumentList.Add("/time:60");
        start.ArgumentList.Add("/v");
        // A fixed prefix also prevents text beginning with '/' from being mistaken for a msg option.
        start.ArgumentList.Add("Nexora: " + message);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(Math.Clamp(job.TimeoutSeconds, 1, 30)));
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            await sink.Log("system", $"[helper] Sending a Windows session message to {job.Host} with the operator's identity.");
            var result = await _run(start, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(result.Output)) await sink.Log("stdout", Limit(result.Output));
            if (!string.IsNullOrWhiteSpace(result.Error)) await sink.Log("stderr", Limit(result.Error));
            return result.ExitCode == 0
                ? Outcomes.Ok($"Windows accepted the message for sessions on {job.Host}. User acknowledgement is not tracked.")
                : Outcomes.Fail($"Windows could not send the message (exit {result.ExitCode}). Check the job output for session, RPC, or permission errors.", result.ExitCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Outcomes.Fail("Windows messaging timed out; delivery could not be confirmed. Check the workstation's session service, RPC connectivity, and the operator's messaging permissions.");
        }
        catch (Win32Exception ex)
        {
            return Outcomes.Fail($"The Windows message command could not start (Windows error {ex.NativeErrorCode}): {ex.Message}", ex.NativeErrorCode);
        }
    }

    private static string Limit(string value)
    {
        var text = value.Trim();
        return text.Length > 16_384 ? text[..16_384] : text;
    }

    private static async Task<MessageCommandResult> RunProcessAsync(ProcessStartInfo start, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The Windows message command could not start.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
            return new MessageCommandResult(process.ExitCode, await output, await error);
        }
        finally
        {
            // Cancellation stops only our local msg client; it cannot retract a popup already accepted by Windows.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            _ = Task.WhenAll(output, error).ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
