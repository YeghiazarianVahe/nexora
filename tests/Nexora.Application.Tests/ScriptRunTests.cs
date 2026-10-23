using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Nexora.Contracts;
using Nexora.Helper.Actions;

namespace Nexora.Application.Tests;

/// <summary>H3 / N8: script.run through an admin-share + SYSTEM task, against a fake PC.</summary>
public sealed class ScriptRunTests
{
    private sealed class Sink : IJobSink
    {
        public List<string> Lines { get; } = [];
        public Task Log(string stream, string line) { lock (Lines) Lines.Add(line); return Task.CompletedTask; }
        public Task Progress(int percent) => Task.CompletedTask;
    }

    /// <summary>A PC that "runs" the task: output arrives in pieces (one cut inside a multi-byte character), then the exit code.</summary>
    private sealed class FakePc : ISystemTaskHost
    {
        public Dictionary<string, byte[]> Files { get; } = [];
        public List<string> Events { get; } = [];
        public string? TaskName, Command, Arguments;
        public byte[] Output = [];
        public string? Exit;
        public int Polls;
        public bool Running = true;
        public Func<FakePc, int, bool>? Advance;     // called per poll; false to stay running forever

        public Task PrepareAsync(string host, string jobId, IReadOnlyDictionary<string, byte[]> files, CancellationToken ct)
        { Events.Add("prepare"); foreach (var f in files) Files[f.Key] = f.Value; return Task.CompletedTask; }
        public Task StartAsync(string host, string taskName, string command, string arguments, TimeSpan limit, CancellationToken ct)
        { Events.Add("start"); TaskName = taskName; Command = command; Arguments = arguments; return Task.CompletedTask; }
        public Task<byte[]?> ReadAsync(string host, string jobId, string file, long offset, CancellationToken ct)
        {
            Polls++;
            Advance?.Invoke(this, Polls);
            if (file == ScriptRunAction.OutputFile) return Task.FromResult<byte[]?>(offset >= Output.Length ? [] : Output[(int)offset..]);
            if (file == ScriptRunAction.ExitFile) return Task.FromResult(Exit is null ? null : (byte[]?)new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(Exit)).ToArray());
            return Task.FromResult<byte[]?>(null);
        }
        public Task<bool> IsRunningAsync(string host, string taskName, CancellationToken ct) => Task.FromResult(Running);
        public Task StopAsync(string host, string taskName, CancellationToken ct) { Events.Add("stop"); return Task.CompletedTask; }
        public Task CleanupAsync(string host, string taskName, string jobId) { Events.Add("cleanup"); return Task.CompletedTask; }
    }

    private static JobPayload Job(string body, string? hash = null, string host = "PC-1") =>
        new(Guid.NewGuid(), Guid.NewGuid(), "script.run", host, "S-1-5-21-1-2-3-1000",
            new() { ["scriptId"] = Guid.NewGuid().ToString(), ["scriptBody"] = body, ["scriptSha256"] = hash ?? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))) },
            120, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(2), "n");

    private static ScriptRunAction Action(FakePc pc) => new(pc, TimeSpan.FromMilliseconds(5));

    [Fact]
    public async Task The_script_runs_as_a_one_off_task_streams_its_output_and_everything_is_cleaned_up()
    {
        var pc = new FakePc();
        var text = "héllo wörld ✓\r\nsecond line\r\n";
        var bytes = new UTF8Encoding(false).GetBytes(text);
        // delivered in three pieces, the first ends in the middle of "ö" and of the last line
        var cuts = new[] { 3, 14, bytes.Length };
        pc.Advance = (p, n) =>
        {
            p.Output = bytes[..cuts[Math.Min(n - 1, 2)]];
            if (n >= 4) p.Exit = "0";
            return true;
        };
        var sink = new Sink();

        var outcome = await Action(pc).RunAsync(Job("Write-Output 'x'"), sink, default);

        outcome.State.Should().Be("Succeeded");
        sink.Lines.Should().Contain("héllo wörld ✓").And.Contain("second line");
        sink.Lines.Count(l => l.Contains("wörld")).Should().Be(1, "no line is sent twice or cut in half");
        pc.Events.Should().Equal("prepare", "start", "cleanup");
        pc.TaskName.Should().StartWith("NexoraJob-");
        pc.Command.Should().Be("powershell.exe");
        pc.Arguments.Should().Contain("-NoProfile").And.Contain("-ExecutionPolicy Bypass").And.Contain(@"C:\ProgramData\Nexora\Jobs\");
        Encoding.UTF8.GetString(pc.Files[ScriptRunAction.ScriptFile]).Should().Contain("Write-Output 'x'");
        pc.Files[ScriptRunAction.ScriptFile].Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
    }

    [Fact]
    public async Task A_non_zero_exit_code_fails_the_target_with_that_code()
    {
        var pc = new FakePc { Exit = "7" };
        var outcome = await Action(pc).RunAsync(Job("exit 7"), new Sink(), default);
        outcome.State.Should().Be("Failed");
        outcome.ExitCode.Should().Be(7);
        pc.Events.Should().EndWith("cleanup");
    }

    [Fact]
    public async Task A_script_that_does_not_match_its_hash_is_refused_before_anything_touches_the_PC()
    {
        var pc = new FakePc();
        var outcome = await Action(pc).RunAsync(Job("Remove-Item C:\\ -Recurse", hash: new string('A', 64)), new Sink(), default);
        outcome.State.Should().Be("Failed");
        outcome.Summary.Should().Contain("hash");
        pc.Events.Should().BeEmpty();

        (await Action(pc).RunAsync(Job(""), new Sink(), default)).State.Should().Be("Failed");
        var noHash = Job("x") with { Parameters = new() { ["scriptBody"] = "x" } };
        (await Action(pc).RunAsync(noHash, new Sink(), default)).State.Should().Be("Failed");
        pc.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task An_invalid_host_is_refused()
    {
        var pc = new FakePc();
        (await Action(pc).RunAsync(Job("x", host: "pc-1; calc"), new Sink(), default)).State.Should().Be("Failed");
        pc.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancel_stops_the_task_and_still_cleans_up()
    {
        var pc = new FakePc();
        using var cts = new CancellationTokenSource();
        pc.Advance = (_, n) => { if (n == 3) cts.Cancel(); return true; };

        var act = () => Action(pc).RunAsync(Job("Start-Sleep 600"), new Sink(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        pc.Events.Should().Equal("prepare", "start", "stop", "cleanup");
    }

    [Fact]
    public async Task A_task_that_vanishes_without_a_result_fails_instead_of_hanging()
    {
        var pc = new FakePc { Running = false };
        var outcome = await Action(pc).RunAsync(Job("x"), new Sink(), default);
        outcome.State.Should().Be("Failed");
        outcome.Summary.Should().Contain("without reporting");
        pc.Events.Should().EndWith("cleanup");
    }

    [Fact]
    public void The_runner_writes_utf8_output_and_the_exit_code_into_the_job_folder_and_holds_nothing_operator_controlled()
    {
        var id = Guid.NewGuid().ToString("D");
        var runner = ScriptRunAction.Runner(id);
        runner.Should().Contain("UTF8Encoding($false)").And.Contain("AppendAllText").And.Contain("exit.txt").And.Contain($@"C:\ProgramData\Nexora\Jobs\{id}");
        runner.Should().NotContain("{{").And.NotContain("scriptBody");
    }

    [Fact]
    public void script_run_is_registered_with_the_helper()
    {
        ActionRegistry.IsKnown("script.run").Should().BeTrue();
    }
}

/// <summary>Opt-in, against a real PC: set NEXORA_LIVE_SCRIPT_HOST (for example localhost) and run with an account that is administrator there.</summary>
public sealed class ScriptRunLiveTests
{
    private sealed class Sink : IJobSink
    {
        public List<string> Lines { get; } = [];
        public Task Log(string stream, string line) { Lines.Add(line); return Task.CompletedTask; }
        public Task Progress(int percent) => Task.CompletedTask;
    }

    [Fact]
    public async Task A_real_system_task_runs_the_script_and_returns_utf8_output()
    {
        var host = Environment.GetEnvironmentVariable("NEXORA_LIVE_SCRIPT_HOST");
        if (string.IsNullOrEmpty(host)) return; // not asked for

        const string body = "Write-Output 'héllo ✓ from' ; whoami ; Write-Error 'oops' ; Write-Output 'done' ; exit 3";
        var job = new JobPayload(Guid.NewGuid(), Guid.NewGuid(), "script.run", host, "S-1-5-21-1-2-3-1000",
            new() { ["scriptId"] = Guid.NewGuid().ToString(), ["scriptBody"] = body, ["scriptSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))) },
            90, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(2), "n");
        var sink = new Sink();

        var outcome = await new ScriptRunAction().RunAsync(job, sink, default);

        Console.WriteLine(string.Join(" | ", sink.Lines));
        outcome.ExitCode.Should().Be(3);
        sink.Lines.Should().Contain(l => l.Contains("héllo ✓"));
        sink.Lines.Should().Contain(l => l.Contains(@"nt authority\system", StringComparison.OrdinalIgnoreCase));
        sink.Lines.Should().Contain("done");
        Directory.Exists($@"\{host}\C$\ProgramData\Nexora\Jobs\{job.TargetId:D}").Should().BeFalse("the job folder is removed");
    }
}
