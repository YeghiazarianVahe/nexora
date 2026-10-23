using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Nexora.Helper.Actions;

namespace Nexora.Application.Tests;

/// <summary>
/// QA-14. The generated body runs locally with chkdsk (via cmd.exe), shutdown.exe and the registry read replaced by PowerShell functions that shadow them,
/// so no real CHKDSK, restart or registry write can happen. "Scheduled" means the BootExecute autocheck entry for the system drive exists.
/// </summary>
public sealed class ChkdskSchedulingTests
{
    private const string Scheduled = @"autocheck autochk /r \??\C:";

    [Theory]
    // chkdsk exit, BootExecute entry, restartNow, shutdown exit, expect shutdown called, expected script exit
    [InlineData(0, Scheduled, false, 0, false, 0)]
    [InlineData(3, Scheduled, false, 0, false, 0)]       // chkdsk's own code is not the signal; the schedule is
    [InlineData(0, Scheduled, true, 0, true, 3010)]
    [InlineData(0, Scheduled, true, 5, true, 2)]          // scheduled, restart failed: distinct failure
    [InlineData(0, "autocheck autochk *", false, 0, false, 1)]      // not scheduled (default entry only)
    [InlineData(0, "autocheck autochk *", true, 0, false, 1)]       // not scheduled: restart must NEVER be attempted
    [InlineData(1, "", true, 0, false, 1)]
    [InlineData(0, @"autocheck autochk /r \??\D:", true, 0, false, 1)]   // another drive scheduled, not the system drive
    public async Task Success_means_the_boot_check_is_scheduled_and_a_restart_follows_only_then(int chkdsk, string bootEntry, bool restart, int shutdownExit, bool shutdownCalled, int exit)
    {
        var (body, error) = SystemCommandAction.Commands["disk.chkdsk.fix"].Build(new Dictionary<string, string> { ["restartNow"] = restart ? "true" : "false" });
        error.Should().BeNull();
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $env:SystemDrive = 'C:'
            function cmd.exe { Write-Output 'QA_CHKDSK_STUB'; $global:LASTEXITCODE = {{chkdsk}} }
            function Get-ItemProperty { [pscustomobject]@{ BootExecute = @('{{bootEntry}}') } }
            function shutdown.exe { Write-Output 'QA_SHUTDOWN'; $global:LASTEXITCODE = {{shutdownExit}} }
            {{body}}
            """;
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorsTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(true); }
        var output = await outputTask;
        process.ExitCode.Should().Be(exit, output + await errorsTask);
        output.Should().Contain("QA_CHKDSK_STUB");   // the stub, not the real tool, ran
        output.Contains("QA_SHUTDOWN").Should().Be(shutdownCalled);
        if (exit == 1) output.Should().NotContain("restarts in 60").And.NotContain("runs at the next restart");
    }

    [Theory]
    [InlineData(0, "Succeeded")]
    [InlineData(3010, "Warning")]
    [InlineData(2, "Failed")]
    [InlineData(1, "Failed")]
    [InlineData(-1, "Failed")]
    public void Exit_codes_map_to_honest_states(int code, string state) =>
        SystemCommandAction.Commands["disk.chkdsk.fix"].Exit!(code).State.Should().Be(state);
}
