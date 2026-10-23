using FluentAssertions;
using Nexora.Application.Jobs;
using Nexora.Contracts;
using Nexora.Helper.Actions;

namespace Nexora.Application.Tests;

/// <summary>The built-in maintenance commands: fixed bodies, strict parameters, honest exit codes.</summary>
public sealed class SystemCommandTests
{
    private sealed class Sink : IJobSink
    {
        public List<string> Lines { get; } = [];
        public Task Log(string stream, string line) { Lines.Add(line); return Task.CompletedTask; }
        public Task Progress(int percent) => Task.CompletedTask;
    }

    private sealed class FakePc : ISystemTaskHost
    {
        public Dictionary<string, byte[]> Files { get; } = [];
        public string Exit = "0";
        public Task PrepareAsync(string host, string jobId, IReadOnlyDictionary<string, byte[]> files, CancellationToken ct) { foreach (var f in files) Files[f.Key] = f.Value; return Task.CompletedTask; }
        public Task StartAsync(string host, string taskName, string command, string arguments, TimeSpan limit, CancellationToken ct) => Task.CompletedTask;
        public Task<byte[]?> ReadAsync(string host, string jobId, string file, long offset, CancellationToken ct) =>
            Task.FromResult<byte[]?>(file == ScriptRunAction.ExitFile ? System.Text.Encoding.UTF8.GetBytes(Exit) : file == ScriptRunAction.OutputFile ? [] : null);
        public Task<bool> IsRunningAsync(string host, string taskName, CancellationToken ct) => Task.FromResult(true);
        public Task StopAsync(string host, string taskName, CancellationToken ct) => Task.CompletedTask;
        public Task CleanupAsync(string host, string taskName, string jobId) => Task.CompletedTask;
    }

    private static JobPayload Job(string code, Dictionary<string, string>? p = null, string host = "PC-1") =>
        new(Guid.NewGuid(), Guid.NewGuid(), code, host, "S-1-5-21-1-2-3-1000", p ?? [], 120, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(2), "n");

    private static (SystemCommandAction Action, FakePc Pc) Make(string exit = "0")
    {
        var pc = new FakePc { Exit = exit };
        return (new SystemCommandAction(new ScriptRunAction(pc, TimeSpan.FromMilliseconds(5))), pc);
    }

    private static string BodyOf(FakePc pc) => System.Text.Encoding.UTF8.GetString(pc.Files[ScriptRunAction.ScriptFile]);

    [Fact]
    public async Task Repair_source_is_passed_literally_without_PowerShell_expansion()
    {
        var source = @"\\server\share\$QA_VALUE\install.wim";
        var (action, pc) = Make();
        await action.RunAsync(Job("repair.dism.restore", new() { ["source"] = source }), new Sink(), default);
        // Only an argument-capture function runs locally. Never invoke DISM or contact this host.
        var body = BodyOf(pc).TrimStart('\uFEFF').Replace("& \"$env:SystemRoot\\System32\\Dism.exe\"", "Invoke-QaDism");
        body.Should().NotContain("Dism.exe");
        var script = "$QA_VALUE = 'expanded'; function Invoke-QaDism { $args | ConvertTo-Json -Compress; $global:LASTEXITCODE = 0 };\n" + body;
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        process.ExitCode.Should().Be(0, await process.StandardError.ReadToEndAsync());
        var args = System.Text.Json.JsonSerializer.Deserialize<string[]>(output)!;
        args.Should().Contain($"/Source:WIM:{source}:1");
    }

    [Fact]
    public void The_catalog_offers_exactly_what_the_helper_can_run()
    {
        ActionCatalog.SystemCommandCodes.Should().BeEquivalentTo(SystemCommandAction.Commands.Keys);
        foreach (var code in ActionCatalog.SystemCommandCodes) ActionCatalog.Find(code)!.Implemented.Should().BeTrue(code);
        ActionRegistry.IsKnown("repair.sfc").Should().BeTrue();
    }

    [Fact]
    public async Task sfc_runs_the_fixed_command_and_removes_the_NUL_characters_it_writes()
    {
        var (action, pc) = Make();
        var outcome = await action.RunAsync(Job("repair.sfc"), new Sink(), default);
        outcome.State.Should().Be("Succeeded");
        BodyOf(pc).Should().Contain("sfc.exe\" /scannow").And.Contain("`0");
    }

    [Theory]
    [InlineData("repair.dism.check", "/CheckHealth")]
    [InlineData("repair.dism.scan", "/ScanHealth")]
    [InlineData("repair.dism.cleanup", "/StartComponentCleanup")]
    [InlineData("repair.dism.restore", "/RestoreHealth")]
    [InlineData("disk.chkdsk.scan", "/scan")]
    [InlineData("net.gpupdate", "/force")]
    [InlineData("net.flushdns", "/flushdns")]
    [InlineData("system.timesync", "/resync")]
    public async Task Each_command_is_its_own_fixed_line(string code, string expected)
    {
        var (action, pc) = Make();
        (await action.RunAsync(Job(code), new Sink(), default)).State.Should().Be("Succeeded");
        BodyOf(pc).Should().Contain(expected);
    }

    [Fact]
    public async Task DISM_exit_codes_read_honestly()
    {
        (await Make("0").Action.RunAsync(Job("repair.dism.scan"), new Sink(), default)).State.Should().Be("Succeeded");
        var restart = await Make("3010").Action.RunAsync(Job("repair.dism.scan"), new Sink(), default);
        (restart.State, restart.ExitCode).Should().Be(("Warning", 3010));
        var failed = await Make("-2146498529").Action.RunAsync(Job("repair.dism.scan"), new Sink(), default);
        failed.State.Should().Be("Failed");
        failed.Summary.Should().Contain("0x800F081F");
    }

    [Fact]
    public async Task chkdsk_scan_distinguishes_clean_fixed_and_failed()
    {
        (await Make("0").Action.RunAsync(Job("disk.chkdsk.scan"), new Sink(), default)).State.Should().Be("Succeeded");
        (await Make("1").Action.RunAsync(Job("disk.chkdsk.scan"), new Sink(), default)).State.Should().Be("Warning");
        (await Make("3").Action.RunAsync(Job("disk.chkdsk.scan"), new Sink(), default)).State.Should().Be("Failed");
    }

    [Theory]
    [InlineData("\\\\server\\share\\sources\\install.wim", "WIM")]
    [InlineData("\\\\server\\share\\x\\install.esd", "ESD")]
    public async Task A_repair_source_is_a_UNC_wim_or_esd_and_is_only_ever_used_inside_quotes(string source, string kind)
    {
        var (action, pc) = Make();
        (await action.RunAsync(Job("repair.dism.restore", new() { ["source"] = source, ["limitAccess"] = "true" }), new Sink(), default)).State.Should().Be("Succeeded");
        BodyOf(pc).Should().Contain($"'/Source:{kind}:{source}:1'").And.Contain("/LimitAccess");
    }

    [Theory]
    [InlineData("C:\\local\\install.wim")]                                       // not UNC
    [InlineData("\\\\server\\share\\install.wim\" ; calc ; \"")]                 // quote break-out
    [InlineData("\\\\server\\share\\install.exe")]                               // wrong kind of file
    [InlineData("\\\\server\\share\\a\u0000b\\install.wim")]
    public async Task Anything_else_as_a_repair_source_is_refused_before_a_task_is_made(string source)
    {
        var (action, pc) = Make();
        var outcome = await action.RunAsync(Job("repair.dism.restore", new() { ["source"] = source }), new Sink(), default);
        outcome.State.Should().Be("Failed");
        pc.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_code_or_a_bad_host_runs_nothing()
    {
        var (action, pc) = Make();
        (await action.RunAsync(Job("format.disk"), new Sink(), default)).State.Should().Be("Failed");
        (await action.RunAsync(Job("repair.sfc", host: "pc; calc"), new Sink(), default)).State.Should().Be("Failed");
        pc.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task chkdsk_fix_schedules_for_the_next_restart_or_restarts_in_60_seconds_when_asked()
    {
        var (a, pc1) = Make();
        await a.RunAsync(Job("disk.chkdsk.fix"), new Sink(), default);
        BodyOf(pc1).Should().Contain("chkdsk.exe %SystemDrive% /f /r").And.NotContain("shutdown");
        var (b, pc2) = Make();
        await b.RunAsync(Job("disk.chkdsk.fix", new() { ["restartNow"] = "true" }), new Sink(), default);
        BodyOf(pc2).Should().Contain("shutdown.exe /r /t 60");
    }
}

/// <summary>Opt-in, against a real PC: NEXORA_LIVE_SCRIPT_HOST=localhost, run as an administrator there. Only harmless commands.</summary>
public sealed class SystemCommandLiveTests
{
    private sealed class Sink : IJobSink
    {
        public List<string> Lines { get; } = [];
        public Task Log(string stream, string line) { Lines.Add(line); return Task.CompletedTask; }
        public Task Progress(int percent) => Task.CompletedTask;
    }

    [Theory]
    [InlineData("system.pendingreboot", "RebootRequired:")]
    [InlineData("net.flushdns", "Successfully flushed")]
    [InlineData("net.gpresult", "RSOP")]
    [InlineData("update.history", "")]
    [InlineData("repair.dism.check", "completed successfully")]
    public async Task A_real_system_task_runs_the_command(string code, string expectedText)
    {
        var host = Environment.GetEnvironmentVariable("NEXORA_LIVE_SCRIPT_HOST");
        if (string.IsNullOrEmpty(host)) return;
        var sink = new Sink();
        var job = new JobPayload(Guid.NewGuid(), Guid.NewGuid(), code, host, "S-1-5-21-1-2-3-1000", [], 120, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(2), "n");

        var outcome = await new SystemCommandAction().RunAsync(job, sink, default);

        Console.WriteLine(code + ": " + string.Join(" | ", sink.Lines.Take(6)));
        outcome.State.Should().NotBe("Failed", outcome.Summary);
        if (expectedText != "") string.Join("\n", sink.Lines).Should().Contain(expectedText);
    }
}
