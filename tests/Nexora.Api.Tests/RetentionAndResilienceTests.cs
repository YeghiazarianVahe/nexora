using System.Net;
using FluentAssertions;
using Nexora.Api.Services;
using Nexora.Application;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Api.Tests;

/// <summary>M14, M6, L1, L9: retention, per-host deadlines, strict host names, and no raw exception text in answers.</summary>
public sealed class RetentionAndResilienceTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    [Fact]
    public async Task Jobs_finished_more_than_90_days_ago_are_purged_with_their_output_but_the_audit_log_is_kept()
    {
        var now = DateTime.UtcNow;
        Guid oldJob = Guid.NewGuid(), newJob = Guid.NewGuid(), runningJob = Guid.NewGuid(), oldTarget = Guid.NewGuid(), newTarget = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
            Job J(Guid id, JobState st, DateTime? finished) => new() { Id = id, ActionCode = "demo.simulate", InitiatorSid = "S", InitiatorAccount = "T", CreatedUtc = now.AddDays(-200), Status = st, FinishedUtc = finished };
            db.Jobs.AddRange(J(oldJob, JobState.Succeeded, now.AddDays(-91)), J(newJob, JobState.Succeeded, now.AddDays(-89)), J(runningJob, JobState.Running, null));
            db.JobTargets.Add(new JobTarget { Id = oldTarget, JobId = oldJob, EndpointId = Guid.NewGuid(), EndpointName = "a", Host = "a", Status = TargetState.Succeeded });
            db.JobTargets.Add(new JobTarget { Id = newTarget, JobId = newJob, EndpointId = Guid.NewGuid(), EndpointName = "b", Host = "b", Status = TargetState.Succeeded });
            db.JobLogChunks.Add(new JobLogChunk { JobTargetId = oldTarget, Seq = 1, TimestampUtc = now, Text = "old output" });
            db.JobLogChunks.Add(new JobLogChunk { JobTargetId = newTarget, Seq = 1, TimestampUtc = now, Text = "new output" });
            db.AuditEntries.Add(new AuditEntry { Action = "job.create", TargetType = "Job", TargetId = oldJob.ToString(), TimestampUtc = now.AddDays(-91), ActorSid = "S", ActorAccount = "T" });
            await db.SaveChangesAsync();

            await MonitoringService.PurgeJobsAsync(db, now, 90, default);
        }

        factory.Query(db => db.Jobs.Any(j => j.Id == oldJob)).Should().BeFalse();
        factory.Query(db => db.JobTargets.Any(t => t.Id == oldTarget)).Should().BeFalse();
        factory.Query(db => db.JobLogChunks.Any(l => l.JobTargetId == oldTarget)).Should().BeFalse();
        factory.Query(db => db.Jobs.Any(j => j.Id == newJob) && db.JobTargets.Any(t => t.Id == newTarget) && db.JobLogChunks.Any(l => l.JobTargetId == newTarget)).Should().BeTrue("89 days is inside the window");
        factory.Query(db => db.Jobs.Any(j => j.Id == runningJob)).Should().BeTrue("a job that has not finished is never purged");
        factory.Query(db => db.AuditEntries.Any(a => a.TargetId == oldJob.ToString())).Should().BeTrue("the audit log is kept");
    }

    [Fact]
    public async Task A_host_that_never_answers_is_abandoned_at_its_deadline_and_the_cycle_moves_on()
    {
        var never = new TaskCompletionSource<int>(); // a WMI call that ignores cancellation
        var started = DateTime.UtcNow;

        var act = async () => await MonitoringService.WithDeadline(never.Task, TimeSpan.FromMilliseconds(200), "wks-hung");

        await act.Should().ThrowAsync<TimeoutException>().WithMessage("*wks-hung*");
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(3));
        never.SetException(new InvalidOperationException("late failure")); // must not surface anywhere
        (await MonitoringService.WithDeadline(Task.FromResult(7), TimeSpan.FromSeconds(1), "fast")).Should().Be(7);
    }

    [Theory]
    [InlineData("wks-44", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("wks-44\n", false)]
    [InlineData("wks-44\r\nevil", false)]
    [InlineData("-bad", false)]
    public void A_host_name_must_match_to_the_very_end_not_up_to_a_trailing_newline(string host, bool valid) =>
        HostnameRules.IsValid(host).Should().Be(valid);

    [Fact]
    public async Task A_failed_query_tells_the_browser_what_to_check_but_never_the_raw_exception()
    {
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var id = await factory.SeedEndpointAsync("PC-FAILS");
        FakeCollector.Failing.Add(factory.Query(db => db.Endpoints.Single(e => e.Id == id).Hostname));

        var response = await viewer.GetAsync($"/api/endpoints/{id}/detail");
        var text = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        text.Should().NotContain("10.99.99.99").And.NotContain("secret-detail").And.NotContain("internal-share");
        text.Should().Contain("server log");
    }
}

public sealed class AccountResolutionTests
{
    [Fact]
    public void A_failed_lookup_is_not_remembered_so_a_later_success_is_used()
    {
        var cache = new System.Collections.Concurrent.ConcurrentDictionary<string, string?>();
        var reachable = false;
        string? Server(string s) => reachable ? @"CONTOSO\ani" : null;

        Nexora.Infrastructure.Wmi.AccountResolution.Resolve("S-1-5-21-1-2-3-1100", cache, Server, _ => null).Should().BeNull();
        cache.ContainsKey("S-1-5-21-1-2-3-1100").Should().BeFalse("a failure must not turn a live account into a deleted one for good");

        reachable = true;
        Nexora.Infrastructure.Wmi.AccountResolution.Resolve("S-1-5-21-1-2-3-1100", cache, Server, _ => null).Should().Be(@"CONTOSO\ani");
        Nexora.Infrastructure.Wmi.AccountResolution.Resolve("S-1-5-21-1-2-3-1100", cache, _ => throw new InvalidOperationException("not asked again"), _ => null).Should().Be(@"CONTOSO\ani");
    }

    [Fact]
    public void A_local_account_the_server_cannot_see_is_resolved_by_asking_the_target()
    {
        var cache = new System.Collections.Concurrent.ConcurrentDictionary<string, string?>();
        var asked = new List<string>();
        var name = Nexora.Infrastructure.Wmi.AccountResolution.Resolve("S-1-5-21-9-9-9-1001", cache, _ => null, s => { asked.Add(s); return @"WKS-44\localadmin"; });
        name.Should().Be(@"WKS-44\localadmin");
        asked.Should().Equal("S-1-5-21-9-9-9-1001");
    }
}

public sealed class LocalHostTests
{
    private static readonly HashSet<string> Mine = new(StringComparer.OrdinalIgnoreCase) { "10.20.30.111", "fe80::1" };

    [Theory]
    [InlineData("wks-111", true)]
    [InlineData("WKS-111", true)]
    [InlineData("wks-111.contoso.example", true)]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.20.30.111", true)]
    [InlineData("wks-44", false)]
    [InlineData("wks-44.contoso.example", false)]
    [InlineData("10.20.30.44", false)]
    [InlineData("", false)]
    [InlineData("10", false)]
    public void This_computer_is_recognised_by_name_fqdn_and_address(string host, bool expected) =>
        Nexora.Infrastructure.Wmi.LocalHost.Is(host, "WKS-111", Mine).Should().Be(expected);
}
