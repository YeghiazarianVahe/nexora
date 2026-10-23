using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Nexora.Contracts;
using Nexora.Helper;
using Nexora.Helper.Actions;

namespace Nexora.Application.Tests;

/// <summary>diag.collect: allow-list, hash check, per-collector timeout and the line format, against a fake task host.</summary>
public sealed class HelperDiagCollectTests
{
    private sealed class FakeTasks : ISystemTaskHost
    {
        public Dictionary<string, string> OutputByBody { get; } = [];
        public HashSet<string> Hang { get; } = [];
        public HashSet<string> Existing { get; } = [];
        public List<string> Prepared { get; } = [];
        public List<string> PreparedIds { get; } = [];
        private readonly Dictionary<string, string> _bodies = [];

        public Task PrepareAsync(string host, string jobId, IReadOnlyDictionary<string, byte[]> files, CancellationToken ct)
        {
            var body = Encoding.UTF8.GetString(files["script.ps1"]).TrimStart('\uFEFF');
            _bodies[jobId] = body;
            Prepared.Add(body);
            PreparedIds.Add(jobId);
            return Task.CompletedTask;
        }
        public Task StartAsync(string host, string taskName, string command, string arguments, TimeSpan limit, CancellationToken ct) => Task.CompletedTask;
        public Task<byte[]?> ReadAsync(string host, string jobId, string file, long offset, CancellationToken ct)
        {
            if (Existing.Contains(jobId)) return Task.FromResult<byte[]?>([]);
            if (!_bodies.TryGetValue(jobId, out var body)) return Task.FromResult<byte[]?>(null);
            if (Hang.Contains(body)) return Task.FromResult<byte[]?>(null);
            if (file == "exit.txt") return Task.FromResult<byte[]?>(Encoding.UTF8.GetBytes("0"));
            var all = Encoding.UTF8.GetBytes(OutputByBody.GetValueOrDefault(body, "") + "\r\n");
            return Task.FromResult<byte[]?>(offset >= all.Length ? [] : all[(int)offset..]);
        }
        public Task<bool> IsRunningAsync(string host, string taskName, CancellationToken ct) =>
            Task.FromResult(_bodies.TryGetValue(taskName.Replace("NexoraJob-", ""), out var body) && Hang.Contains(body));
        public Task StopAsync(string host, string taskName, CancellationToken ct) => Task.CompletedTask;
        public Task CleanupAsync(string host, string taskName, string jobId) { _bodies.Remove(jobId); return Task.CompletedTask; }
    }

    private sealed class Sink : IJobSink
    {
        public List<string> Stdout { get; } = [];
        public Task Log(string stream, string line) { if (stream == "stdout") Stdout.Add(line); return Task.CompletedTask; }
        public Task Progress(int percent) => Task.CompletedTask;
    }

    private static string Sha(string body) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    private static JobPayload Job(params (string Id, string Body, string? Hash, int Timeout)[] collectors) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "diag.collect", "PC-1", "S-1-5-21-1-2-3-1000",
            new Dictionary<string, string>
            {
                ["collectors"] = JsonSerializer.Serialize(collectors.Select(c => new { id = c.Id, sha256 = c.Hash ?? Sha(c.Body), body = c.Body, timeoutSeconds = c.Timeout })),
            }, 120, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(2), "n");

    private static DiagCollectAction Action(FakeTasks t, params string[] allowed) => new(t, TimeSpan.FromMilliseconds(5), allowed);

    [Fact]
    public async Task The_shipped_allow_list_contains_collectors_but_refuses_unknown_ids_without_touching_the_PC()
    {
        DiagCollectors.Ids.Should().Contain("system-resources").And.Contain("workstation-checks");
        var t = new FakeTasks();

        var outcome = await new DiagCollectAction(t, TimeSpan.FromMilliseconds(5)).RunAsync(Job(("os", "Write-Output 1", null, 5)), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Failed");
        outcome.Summary.Should().Contain("not known");
        t.Prepared.Should().BeEmpty();
    }

    [Fact]
    public async Task A_known_id_with_a_substituted_body_is_refused_even_with_a_matching_supplied_hash()
    {
        var t = new FakeTasks();
        var result = await new DiagCollectAction(t).RunAsync(Job(("system-resources", "Write-Output 1", null, 5)), new Sink(), CancellationToken.None);
        result.State.Should().Be("Failed");
        result.Summary.Should().Contain("approved");
        t.Prepared.Should().BeEmpty();
    }

    [Fact]
    public void Every_shipped_server_collector_has_the_same_approved_hash_in_the_helper()
    {
        var catalog = Nexora.Application.Diagnostics.CollectorCatalog.FromAssembly(typeof(Nexora.Application.Diagnostics.CollectorCatalog).Assembly);
        DiagCollectors.Ids.Should().BeEquivalentTo(catalog.Ids);
        foreach (var id in catalog.Ids)
            DiagCollectors.Approves(id, catalog.Find(id)!.Sha256).Should().BeTrue(id);
    }

    [Fact]
    public async Task One_unknown_id_refuses_the_whole_job_before_any_collector_runs()
    {
        var t = new FakeTasks();

        var outcome = await Action(t, "os").RunAsync(Job(("os", "A", null, 5), ("evil", "B", null, 5)), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Failed");
        outcome.Summary.Should().Contain("evil");
        t.Prepared.Should().BeEmpty();
    }

    [Theory]
    [InlineData("OS")]
    [InlineData("../os")]
    [InlineData("os x")]
    [InlineData("")]
    public async Task Malformed_ids_are_refused(string id)
    {
        var t = new FakeTasks();
        var outcome = await Action(t, "os", id).RunAsync(Job((id, "A", null, 5)), new Sink(), CancellationToken.None);
        outcome.State.Should().Be("Failed");
        t.Prepared.Should().BeEmpty();
    }

    [Fact]
    public async Task A_body_that_does_not_match_its_hash_is_refused()
    {
        var t = new FakeTasks();

        var outcome = await Action(t, "os").RunAsync(Job(("os", "Write-Output 1", Sha("something else"), 5)), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Failed");
        outcome.Summary.Should().Contain("hash");
        t.Prepared.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_or_malformed_collector_lists_are_refused()
    {
        var t = new FakeTasks();
        var job = Job() with { Parameters = new Dictionary<string, string> { ["collectors"] = "not json" } };
        (await Action(t, "os").RunAsync(job, new Sink(), CancellationToken.None)).State.Should().Be("Failed");
        (await Action(t, "os").RunAsync(job with { Parameters = new Dictionary<string, string>() }, new Sink(), CancellationToken.None)).State.Should().Be("Failed");
        t.Prepared.Should().BeEmpty();
    }

    [Fact]
    public async Task An_allowed_collector_emits_one_tagged_line_with_its_json()
    {
        var t = new FakeTasks();
        t.OutputByBody["BODY-OS"] = "{\"caption\":\"Windows 11\"}";
        var sink = new Sink();

        var outcome = await Action(t, "os").RunAsync(Job(("os", "BODY-OS", null, 5)), sink, CancellationToken.None);

        outcome.State.Should().Be("Succeeded");
        sink.Stdout.Should().Equal("##NXC os {\"caption\":\"Windows 11\"}");
    }

    [Fact]
    public async Task A_collector_that_times_out_yields_an_incomplete_line_and_the_next_one_still_runs()
    {
        var t = new FakeTasks();
        t.Hang.Add("SLOW");
        t.OutputByBody["FAST"] = "{\"ok\":true}";
        var sink = new Sink();

        var outcome = await Action(t, "slow", "fast").RunAsync(Job(("slow", "SLOW", null, 1), ("fast", "FAST", null, 5)), sink, CancellationToken.None);

        outcome.State.Should().Be("Warning", "an incomplete collector is not a job failure");
        sink.Stdout.Should().HaveCount(2);
        sink.Stdout[0].Should().StartWith("##NXC slow ").And.Contain("\"incomplete\":true").And.Contain("timeout");
        sink.Stdout[1].Should().Be("##NXC fast {\"ok\":true}");
    }

    [Fact]
    public async Task A_result_over_4000_characters_becomes_an_incomplete_line_never_cut_JSON()
    {
        var t = new FakeTasks();
        t.OutputByBody["BIG"] = "{\"v\":\"" + new string('x', 9000) + "\"}";
        var sink = new Sink();

        var outcome = await Action(t, "big").RunAsync(Job(("big", "BIG", null, 5)), sink, CancellationToken.None);

        outcome.State.Should().Be("Warning");
        sink.Stdout.Should().Equal("##NXC big {\"incomplete\":true,\"reason\":\"too large\"}");
    }

    [Fact]
    public async Task Output_over_the_capture_cap_is_incomplete()
    {
        var t = new FakeTasks();
        t.OutputByBody["HUGE"] = string.Join("\r\n", Enumerable.Repeat(new string('y', 1000), 80));
        var sink = new Sink();

        await Action(t, "huge").RunAsync(Job(("huge", "HUGE", null, 5)), sink, CancellationToken.None);

        sink.Stdout.Single().Should().Contain("too large");
    }

    [Fact]
    public async Task Every_collector_gets_its_own_run_id_distinct_from_the_job_target_id()
    {
        var t = new FakeTasks();
        t.OutputByBody["A"] = "{}"; t.OutputByBody["B"] = "{}";
        var job = Job(("a", "A", null, 5), ("b", "B", null, 5));

        await Action(t, "a", "b").RunAsync(job, new Sink(), CancellationToken.None);

        t.PreparedIds.Should().HaveCount(2).And.OnlyHaveUniqueItems();
        t.PreparedIds.Should().NotContain(job.TargetId.ToString("D"));
        t.PreparedIds.Should().Equal(DiagCollectAction.RunId(job.TargetId, 0).ToString("D"), DiagCollectAction.RunId(job.TargetId, 1).ToString("D"));
    }

    [Fact]
    public async Task A_collector_whose_job_folder_already_exists_is_not_run()
    {
        var t = new FakeTasks();
        var job = Job(("a", "A", null, 5), ("b", "B", null, 5));
        t.Existing.Add(DiagCollectAction.RunId(job.TargetId, 0).ToString("D"));
        t.OutputByBody["B"] = "{\"ok\":1}";
        var sink = new Sink();

        var outcome = await Action(t, "a", "b").RunAsync(job, sink, CancellationToken.None);

        outcome.State.Should().Be("Warning");
        t.Prepared.Should().Equal("B");
        sink.Stdout[0].Should().StartWith("##NXC a ").And.Contain("job folder exists");
        sink.Stdout[1].Should().Be("##NXC b {\"ok\":1}");
    }

    [Fact]
    public async Task Output_that_is_not_json_is_reported_as_incomplete_not_passed_on()
    {
        var t = new FakeTasks();
        t.OutputByBody["BAD"] = "oops this is not json";
        var sink = new Sink();

        await Action(t, "bad").RunAsync(Job(("bad", "BAD", null, 5)), sink, CancellationToken.None);

        sink.Stdout.Single().Should().Contain("\"incomplete\":true").And.NotContain("oops");
    }

    [Fact]
    public void The_helper_knows_diag_collect_and_an_unknown_code_has_a_stable_refusal_reason()
    {
        ActionRegistry.IsKnown("diag.collect").Should().BeTrue();
        ActionRegistry.IsKnown("diag.future").Should().BeFalse();
        HelperJobs.Describe(JobTrust.Error.UnknownAction).Should().Be("this helper does not know that action.");
    }
}
