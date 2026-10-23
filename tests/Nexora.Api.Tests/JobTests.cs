using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Nexora.Application;
using Nexora.Application.Jobs;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Api.Tests;

public sealed class JobTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    // ---------- helpers

    private static object P(string outcome = "success", int steps = 2, int delayMs = 20) => new { steps, delayMs, outcome };

    /// <summary>Runs the confirm step when the action needs it, then creates the job. Returns the create response.</summary>
    private static async Task<HttpResponseMessage> RunAsync(
        HttpClient c, string action, Guid[] endpoints, object? parameters = null, string? reason = null, string? typed = null,
        bool overrideProtected = false, bool confirm = true)
    {
        string? token = null;
        var def = ActionCatalog.Find(action);
        if (confirm && def?.NeedsConfirmation == true)
        {
            var confirmed = await c.PostAsJsonAsync("/api/jobs/confirm",
                new { actionCode = action, endpointIds = endpoints, parameters, reason, typed, overrideProtected });
            if (!confirmed.IsSuccessStatusCode) return confirmed;
            token = (await confirmed.Content.ReadFromJsonAsync<ConfirmResponse>())!.Token;
        }
        return await c.PostAsJsonAsync("/api/jobs", new { actionCode = action, endpointIds = endpoints, parameters, reason, confirmToken = token, overrideProtected });
    }

    private static async Task<JobDetail> CreatedAsync(HttpResponseMessage r)
    {
        r.StatusCode.Should().Be(HttpStatusCode.Accepted, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<JobDetail>())!;
    }

    private static async Task<JobDetail> WaitAsync(HttpClient c, Guid id, Func<JobDetail, bool> done, int seconds = 15)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        JobDetail? last = null;
        while (DateTime.UtcNow < until)
        {
            last = await c.GetFromJsonAsync<JobDetail>($"/api/jobs/{id}");
            if (done(last!)) return last!;
            await Task.Delay(40);
        }
        throw new TimeoutException($"Job {id} did not reach the expected state; last: {last?.Job.Status} {last?.Targets.Select(t => t.Status).ToArray().Length}");
    }

    private static bool Finished(JobDetail d) => d.Job.Status is "Succeeded" or "Warning" or "PartiallyFailed" or "Failed" or "Cancelled";

    // ---------- catalog

    [Fact]
    public async Task The_catalog_marks_which_actions_the_callers_role_may_run()
    {
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);

        var v = (await viewer.GetFromJsonAsync<List<CatalogEntry>>("/api/jobs/catalog"))!;
        var o = (await op.GetFromJsonAsync<List<CatalogEntry>>("/api/jobs/catalog"))!;

        v.Should().Contain(e => e.Code == "system.pendingreboot" && e.Allowed);
        v.Should().Contain(e => e.Code == "power.restart" && !e.Allowed);
        o.Should().Contain(e => e.Code == "power.restart" && e.Allowed && e.NeedsReason && e.NeedsTypedConfirmation && e.Risk == "High");
        o.Should().Contain(e => e.Code == "update.install" && !e.Allowed && e.Risk == "Critical");
        o.Should().Contain(e => e.Code == "repair.sfc" && e.Heavy && e.TimeoutSeconds == 3600);
        o.Should().Contain(e => e.Code == "repair.dism.restore" && e.TimeoutSeconds == 7200);
        o.Should().Contain(e => e.Code == "update.scan" && e.TimeoutSeconds == 1800);
        var ready = ActionCatalog.SystemCommandCodes.Concat(new[] { "profile.sizes", "profile.delete", "svc.start", "svc.stop", "svc.restart", "proc.kill", "session.rdp", "session.assist", "session.message", "script.run" });
        o.Where(e => !e.Code.StartsWith("demo.") && !ready.Contains(e.Code)).Should().OnlyContain(e => !e.Implemented, "real actions arrive step by step");
    }

    [Fact]
    public Task The_catalog_has_no_free_form_command_action()
    {
        var codes = ActionCatalog.All.Select(a => a.Code).ToList();
        codes.Should().OnlyHaveUniqueItems();
        ActionCatalog.All.SelectMany(a => a.Parameters).Select(p => p.Name).Should().NotContain(["command", "cmd", "commandLine", "script", "host", "hostname", "ip"]);
        return Task.CompletedTask;
    }

    // ---------- authorization

    [Fact]
    public async Task A_viewer_cannot_create_operator_jobs()
    {
        var ep = await factory.SeedEndpointAsync(online: true);
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        (await RunAsync(viewer, "demo.simulate", [ep], P())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_operator_cannot_run_critical_actions()
    {
        var ep = await factory.SeedEndpointAsync(online: true);
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        (await RunAsync(op, "demo.simulate.critical", [ep], P(), reason: "r", typed: "x")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await RunAsync(op, "update.install", [ep], reason: "r", typed: "x")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Role_less_users_and_anonymous_callers_cannot_use_jobs()
    {
        (await factory.ClientFor(null).GetAsync("/api/jobs")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var none = await factory.UserClientAsync(TestIdentities.NewSid(), role: null);
        (await none.GetAsync("/api/jobs/catalog")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Targets_must_be_inventory_endpoints_and_the_client_cannot_supply_a_host()
    {
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);

        (await RunAsync(op, "demo.simulate", [Guid.NewGuid()], P())).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RunAsync(op, "demo.simulate", [], P())).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // An extra host/ip field is not part of the contract and changes nothing: only endpointIds are used.
        var r = await op.PostAsJsonAsync("/api/jobs", new { actionCode = "demo.simulate", host = "evil.example", ip = "8.8.8.8", parameters = P() });
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Parameters_are_validated_against_the_actions_schema()
    {
        var ep = await factory.SeedEndpointAsync(online: true);
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);

        (await RunAsync(op, "demo.simulate", [ep], new { steps = 99 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RunAsync(op, "demo.simulate", [ep], new { outcome = "explode" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RunAsync(op, "demo.simulate", [ep], new { steps = 2, extra = "x" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RunAsync(op, "demo.simulate", [ep], new { steps = "two" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RunAsync(op, "no.such.action", [ep])).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Critical_services_and_system_processes_are_refused_by_the_schema()
    {
        var ep = await factory.SeedEndpointAsync(online: true);
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);

        (await RunAsync(op, "svc.stop", [ep], new { serviceName = "RpcSs" }, confirm: false)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RunAsync(op, "svc.restart", [ep], new { serviceName = "windefend" }, confirm: false)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RunAsync(op, "proc.kill", [ep], new { pid = 600, processName = "lsass.exe" }, confirm: false)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RunAsync(op, "svc.stop", [ep], new { serviceName = "x\"; calc" }, confirm: false)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RunAsync(op, "repair.dism.restore", [ep], new { source = "C:\\notunc" }, reason: "r", typed: "x", confirm: false)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---------- risk confirmation

    [Fact]
    public async Task A_high_action_needs_a_valid_confirm_token()
    {
        var ep = await factory.SeedEndpointAsync(online: true);
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);

        var none = await RunAsync(op, "demo.simulate.high", [ep], P(), reason: "maintenance", confirm: false);
        none.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await none.Content.ReadAsStringAsync()).Should().Contain("Confirmation");

        var forged = await op.PostAsJsonAsync("/api/jobs", new { actionCode = "demo.simulate.high", endpointIds = new[] { ep }, parameters = P(), reason = "x", confirmToken = "AAAA.BBBB" });
        forged.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_confirm_token_is_bound_to_the_exact_request_and_works_once()
    {
        var a = await factory.SeedEndpointAsync(online: true);
        var b = await factory.SeedEndpointAsync(online: true);
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var name = factory.Query(db => db.Endpoints.Single(e => e.Id == a).Hostname);

        var token = (await (await op.PostAsJsonAsync("/api/jobs/confirm",
            new { actionCode = "demo.simulate.high", endpointIds = new[] { a }, parameters = P(), reason = "x", typed = name })).Content.ReadFromJsonAsync<ConfirmResponse>())!.Token;

        // different target
        (await op.PostAsJsonAsync("/api/jobs", new { actionCode = "demo.simulate.high", endpointIds = new[] { b }, parameters = P(), reason = "x", confirmToken = token }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // different parameters
        (await op.PostAsJsonAsync("/api/jobs", new { actionCode = "demo.simulate.high", endpointIds = new[] { a }, parameters = P("fail"), reason = "x", confirmToken = token }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // the real one works, once
        var body = new { actionCode = "demo.simulate.high", endpointIds = new[] { a }, parameters = P(), reason = "x", confirmToken = token };
        (await op.PostAsJsonAsync("/api/jobs", body)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await op.PostAsJsonAsync("/api/jobs", body)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "tokens are single use");
    }

    [Fact]
    public async Task A_token_issued_to_someone_else_is_useless()
    {
        var ep = await factory.SeedEndpointAsync(online: true);
        var alice = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var mallory = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var name = factory.Query(db => db.Endpoints.Single(e => e.Id == ep).Hostname);

        var token = (await (await alice.PostAsJsonAsync("/api/jobs/confirm",
            new { actionCode = "demo.simulate.medium", endpointIds = new[] { ep }, parameters = P() })).Content.ReadFromJsonAsync<ConfirmResponse>())!.Token;

        (await mallory.PostAsJsonAsync("/api/jobs", new { actionCode = "demo.simulate.medium", endpointIds = new[] { ep }, parameters = P(), confirmToken = token }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _ = name;
    }

    [Fact]
    public async Task High_actions_need_a_reason_and_the_typed_hostname_or_target_count()
    {
        var a = await factory.SeedEndpointAsync(online: true);
        var b = await factory.SeedEndpointAsync(online: true);
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var host = factory.Query(db => db.Endpoints.Single(e => e.Id == a).Hostname);

        (await RunAsync(op, "demo.simulate.high", [a], P(), reason: null, typed: host)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RunAsync(op, "demo.simulate.high", [a], P(), reason: "x", typed: "wrong")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RunAsync(op, "demo.simulate.high", [a, b], P(), reason: "x", typed: "1")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await RunAsync(op, "demo.simulate.high", [a], P(), reason: "x", typed: host.ToLower())).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await RunAsync(op, "demo.simulate.high", [a, b], P(), reason: "x", typed: "2")).StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    // ---------- protected endpoints

    [Fact]
    public async Task Power_actions_on_a_protected_endpoint_are_blocked_without_an_admin_override()
    {
        var server = await factory.SeedEndpointAsync(online: true);
        factory.Query(db => { var e = db.Endpoints.Single(x => x.Id == server); e.OsCaption = "Microsoft Windows Server 2022"; db.SaveChangesAsync().GetAwaiter().GetResult(); return 0; });
        var host = factory.Query(db => db.Endpoints.Single(e => e.Id == server).Hostname);
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);

        var blocked = await RunAsync(op, "demo.simulate.high", [server], P(), reason: "x", typed: host);
        blocked.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await blocked.Content.ReadAsStringAsync()).Should().Contain("Protected");

        // an operator cannot override
        (await RunAsync(op, "demo.simulate.high", [server], P(), reason: "x", typed: host, overrideProtected: true)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_admin_can_override_protection_with_a_reason_and_the_override_is_recorded()
    {
        var server = await factory.SeedEndpointAsync(online: true);
        factory.Query(db => { db.Endpoints.Single(x => x.Id == server).OsCaption = "Windows Server 2019"; db.SaveChangesAsync().GetAwaiter().GetResult(); return 0; });
        var host = factory.Query(db => db.Endpoints.Single(e => e.Id == server).Hostname);
        var sid = TestIdentities.NewSid();
        var admin = await factory.UserClientAsync(sid, Role.Admin);

        (await RunAsync(admin, "demo.simulate.high", [server], P(), reason: null, typed: host, overrideProtected: true)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var job = await CreatedAsync(await RunAsync(admin, "demo.simulate.high", [server], P(), reason: "emergency patch window", typed: host, overrideProtected: true));

        job.Job.ProtectedOverride.Should().BeTrue();
        factory.Query(db => db.AuditEntries.Single(a => a.ActorSid == sid && a.Action == "job.create" && a.TargetId == job.Job.Id.ToString()).Details).Should().Contain("\"protectedOverride\":true").And.Contain("emergency patch window");
    }

    [Fact]
    public async Task The_nexora_server_and_the_protected_list_are_always_protected()
    {
        var byList = await factory.SeedEndpointAsync(online: true);
        var machine = await factory.SeedEndpointAsync(online: true);
        factory.Query(db =>
        {
            db.Endpoints.Single(x => x.Id == byList).Hostname = "wks-111";
            db.Endpoints.Single(x => x.Id == machine).Hostname = Environment.MachineName;
            db.SaveChangesAsync().GetAwaiter().GetResult();
            return 0;
        });
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);

        (await RunAsync(op, "demo.simulate.high", [byList], P(), reason: "x", typed: "wks-111")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RunAsync(op, "demo.simulate.high", [machine], P(), reason: "x", typed: Environment.MachineName)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // a non-power, non-critical action is not blocked
        (await RunAsync(op, "demo.simulate", [byList], P())).StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    // ---------- lifecycle with the fake executor

    [Fact]
    public async Task A_successful_job_goes_Queued_Running_Succeeded_with_output_progress_and_audit()
    {
        var ep = await factory.SeedEndpointAsync(online: true);
        var sid = TestIdentities.NewSid();
        var op = await factory.UserClientAsync(sid, Role.Operator, "TEST\\op");

        var created = await CreatedAsync(await RunAsync(op, "demo.simulate", [ep], P("success", steps: 3, delayMs: 60)));
        created.Job.Status.Should().BeOneOf("Queued", "Running");
        created.Targets.Should().ContainSingle().Which.Status.Should().BeOneOf("Queued", "Running");

        var seen = new HashSet<string>();
        var done = await WaitAsync(op, created.Job.Id, d => { seen.Add(d.Targets[0].Status); return Finished(d); });

        done.Job.Status.Should().Be("Succeeded");
        seen.Should().Contain("Running");
        var t = done.Targets.Single();
        (t.Status, t.ExitCode, t.Progress).Should().Be(("Succeeded", 0, 100));
        t.StartedUtc.Should().NotBeNull();
        t.FinishedUtc.Should().BeOnOrAfter(t.StartedUtc!.Value);
        t.OutputSha256.Should().HaveLength(64);

        var log = (await op.GetFromJsonAsync<List<LogLine>>($"/api/jobs/{created.Job.Id}/targets/{t.Id}/log"))!;
        log.Select(l => l.Text).Should().Contain(["step 1 of 3", "step 2 of 3", "step 3 of 3"]);
        log.Select(l => l.Seq).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        (await op.GetFromJsonAsync<List<LogLine>>($"/api/jobs/{created.Job.Id}/targets/{t.Id}/log?after=2"))!.Should().OnlyContain(l => l.Seq > 2);

        var download = await op.GetAsync($"/api/jobs/{created.Job.Id}/targets/{t.Id}/output");
        download.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        (await download.Content.ReadAsStringAsync()).Should().Contain("step 3 of 3");

        var audit = factory.Query(db => db.AuditEntries.Where(a => a.ActorSid == sid && (a.Action == "job.create" || a.Action == "job.target")).OrderBy(a => a.Id).ToList());
        audit.Select(a => a.Action).Should().Equal("job.create", "job.target");
        audit[0].ActorAccount.Should().Be("TEST\\op");
        audit[1].Details.Should().Contain(t.OutputSha256!).And.Contain("\"exitCode\":0");
    }

    [Theory]
    [InlineData("fail", "Failed", "Failed")]
    [InlineData("warning", "Warning", "Warning")]
    public async Task Failed_and_warning_outcomes_are_reported(string outcome, string targetState, string jobState)
    {
        var ep = await factory.SeedEndpointAsync(online: true);
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var created = await CreatedAsync(await RunAsync(op, "demo.simulate", [ep], P(outcome)));

        var done = await WaitAsync(op, created.Job.Id, Finished);

        (done.Job.Status, done.Targets[0].Status).Should().Be((jobState, targetState));
    }

    [Fact]
    public async Task A_hanging_job_times_out_and_says_so()
    {
        var ep = await factory.SeedEndpointAsync(online: true);
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var created = await CreatedAsync(await RunAsync(op, "demo.simulate", [ep], P("hang", steps: 1, delayMs: 10)));

        var done = await WaitAsync(op, created.Job.Id, Finished, seconds: 20);

        done.Targets[0].Status.Should().Be("TimedOut");
        done.Targets[0].ResultSummary.Should().Contain("Timed out");
        done.Job.Status.Should().Be("Failed");
    }

    [Fact]
    public async Task Cancel_stops_a_running_job_and_a_cancelled_job_cannot_be_cancelled_again()
    {
        var ep = await factory.SeedEndpointAsync(online: true);
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var created = await CreatedAsync(await RunAsync(op, "demo.simulate", [ep], P("hang", steps: 1, delayMs: 10)));
        await WaitAsync(op, created.Job.Id, d => d.Targets[0].Status == "Running");

        (await op.PostAsync($"/api/jobs/{created.Job.Id}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var done = await WaitAsync(op, created.Job.Id, Finished);

        done.Job.Status.Should().Be("Cancelled");
        done.Targets[0].Status.Should().Be("Cancelled");
        (await op.PostAsync($"/api/jobs/{created.Job.Id}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        factory.Query(db => db.AuditEntries.Any(a => a.Action == "job.cancel" && a.TargetId == created.Job.Id.ToString())).Should().BeTrue();
    }

    [Fact]
    public async Task Only_the_initiator_or_an_admin_can_cancel()
    {
        var ep = await factory.SeedEndpointAsync(online: true);
        var owner = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var other = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var created = await CreatedAsync(await RunAsync(owner, "demo.simulate", [ep], P("hang", 1, 10)));

        (await other.PostAsync($"/api/jobs/{created.Job.Id}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.PostAsync($"/api/jobs/{created.Job.Id}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await WaitAsync(owner, created.Job.Id, Finished)).Job.Status.Should().Be("Cancelled");
    }

    // ---------- concurrency

    [Fact]
    public async Task A_second_heavy_job_on_the_same_target_is_rejected_until_the_first_finishes()
    {
        var ep = await factory.SeedEndpointAsync(online: true);
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var host = factory.Query(db => db.Endpoints.Single(e => e.Id == ep).Hostname);

        var first = await CreatedAsync(await RunAsync(admin, "demo.simulate.critical", [ep], P("hang", 1, 10), reason: "r", typed: host));
        var second = await RunAsync(admin, "demo.simulate.critical", [ep], P(), reason: "r", typed: host);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await second.Content.ReadAsStringAsync()).Should().Contain("heavy job");

        // light jobs on that target are still fine
        (await RunAsync(admin, "demo.simulate", [ep], P())).StatusCode.Should().Be(HttpStatusCode.Accepted);

        await admin.PostAsync($"/api/jobs/{first.Job.Id}/cancel", null);
        await WaitAsync(admin, first.Job.Id, Finished);
        (await RunAsync(admin, "demo.simulate.critical", [ep], P(), reason: "r", typed: host)).StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Bulk_jobs_respect_the_configured_parallelism()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var targets = new List<Guid>();
        for (var i = 0; i < 6; i++) targets.Add(await factory.SeedEndpointAsync(online: true));

        var settings = (await admin.GetFromJsonAsync<SettingsEnvelope>("/api/settings"))!.Settings;
        settings.JobParallelism = 2;
        (await admin.PutAsJsonAsync("/api/settings", settings)).StatusCode.Should().Be(HttpStatusCode.OK);

        try
        {
            var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
            var created = await CreatedAsync(await RunAsync(op, "demo.simulate", [.. targets], P("success", steps: 3, delayMs: 120)));

            var maxRunning = 0;
            var done = await WaitAsync(op, created.Job.Id, d =>
            {
                maxRunning = Math.Max(maxRunning, d.Targets.Count(t => t.Status == "Running"));
                return Finished(d);
            }, seconds: 30);

            done.Job.Status.Should().Be("Succeeded");
            done.Targets.Should().OnlyContain(t => t.Status == "Succeeded");
            maxRunning.Should().BeInRange(1, 2, "at most 2 targets may run at once");
        }
        finally
        {
            settings.JobParallelism = 10;
            await admin.PutAsJsonAsync("/api/settings", settings);
        }
    }

    [Fact]
    public async Task A_bulk_job_with_mixed_results_is_PartiallyFailed()
    {
        // The same action runs on both; only the parameter differs per job, so use two jobs' outcomes via one failing target:
        // simulate by marking one target failed through the report API after both started.
        var a = await factory.SeedEndpointAsync(online: true);
        var b = await factory.SeedEndpointAsync(online: true);
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var created = await CreatedAsync(await RunAsync(op, "demo.simulate", [a, b], P("hang", 1, 10)));
        var detail = await WaitAsync(op, created.Job.Id, d => d.Targets.All(t => t.Status == "Running"));

        using var scope = factory.Services.CreateScope();
        var reports = scope.ServiceProvider.GetRequiredService<IJobReports>();
        await reports.CompletedAsync(detail.Targets[0].Id, TargetState.Succeeded, 0, "ok", null);
        await reports.CompletedAsync(detail.Targets[1].Id, TargetState.Failed, 1, "boom", null);

        (await op.GetFromJsonAsync<JobDetail>($"/api/jobs/{created.Job.Id}"))!.Job.Status.Should().Be("PartiallyFailed");
    }

    // ---------- visibility

    [Fact]
    public async Task Viewers_and_operators_see_only_their_own_jobs_and_admins_see_everyones()
    {
        var ep = await factory.SeedEndpointAsync(online: true);
        var aliceSid = TestIdentities.NewSid();
        var alice = await factory.UserClientAsync(aliceSid, Role.Operator, "TEST\\alice");
        var bob = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator, "TEST\\bob");
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var aliceJob = await CreatedAsync(await RunAsync(alice, "demo.simulate", [ep], P()));
        var bobJob = await CreatedAsync(await RunAsync(bob, "demo.simulate", [ep], P()));

        var bobSees = (await bob.GetFromJsonAsync<PagedResult<JobSummary>>("/api/jobs?pageSize=200"))!;
        bobSees.Items.Should().OnlyContain(j => j.InitiatorAccount == "TEST\\bob").And.Contain(j => j.Id == bobJob.Job.Id);
        (await bob.GetAsync($"/api/jobs/{aliceJob.Job.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await bob.GetAsync($"/api/jobs/{aliceJob.Job.Id}/targets/{aliceJob.Targets[0].Id}/log")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await bob.GetAsync($"/api/jobs/{aliceJob.Job.Id}/targets/{aliceJob.Targets[0].Id}/output")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var adminSees = (await admin.GetFromJsonAsync<PagedResult<JobSummary>>("/api/jobs?pageSize=200"))!;
        adminSees.Items.Select(j => j.Id).Should().Contain([aliceJob.Job.Id, bobJob.Job.Id]);
        (await admin.GetFromJsonAsync<PagedResult<JobSummary>>("/api/jobs?operator=alice&pageSize=200"))!.Items.Should().OnlyContain(j => j.InitiatorAccount == "TEST\\alice");
        (await admin.GetAsync($"/api/jobs/{aliceJob.Job.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        _ = aliceSid;
    }

    [Fact]
    public async Task The_job_list_filters_by_status_and_action()
    {
        var ep = await factory.SeedEndpointAsync(online: true);
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var ok = await CreatedAsync(await RunAsync(op, "demo.simulate", [ep], P()));
        var bad = await CreatedAsync(await RunAsync(op, "demo.simulate", [ep], P("fail")));
        await WaitAsync(op, ok.Job.Id, Finished);
        await WaitAsync(op, bad.Job.Id, Finished);

        var failed = (await op.GetFromJsonAsync<PagedResult<JobSummary>>("/api/jobs?status=Failed"))!;
        failed.Items.Should().OnlyContain(j => j.Status == "Failed").And.Contain(j => j.Id == bad.Job.Id).And.NotContain(j => j.Id == ok.Job.Id);
        (await op.GetFromJsonAsync<PagedResult<JobSummary>>("/api/jobs?action=demo.simulate.medium"))!.Items.Should().BeEmpty();
    }

    // ---------- executor reports are authenticated

    [Fact]
    public async Task Only_the_initiators_helper_can_report_on_a_job()
    {
        var ep = await factory.SeedEndpointAsync(online: true);
        var sid = TestIdentities.NewSid();
        var op = await factory.UserClientAsync(sid, Role.Operator);
        var created = await CreatedAsync(await RunAsync(op, "demo.simulate", [ep], P("hang", 1, 10)));
        var target = (await WaitAsync(op, created.Job.Id, d => d.Targets[0].Status == "Running")).Targets[0];

        using var scope = factory.Services.CreateScope();
        var reports = scope.ServiceProvider.GetRequiredService<IJobReports>();

        (await reports.CompletedAsync(target.Id, TargetState.Succeeded, 0, "forged", TestIdentities.NewSid())).Kind.Should().Be(ErrorKind.Forbidden);
        (await reports.LogAsync(target.Id, "stdout", ["forged line"], TestIdentities.NewSid())).Kind.Should().Be(ErrorKind.Forbidden);
        (await reports.CompletedAsync(target.Id, TargetState.Succeeded, 0, "mine", sid)).Ok.Should().BeTrue();
        (await reports.CompletedAsync(target.Id, TargetState.Failed, 1, "late", sid)).Ok.Should().BeTrue("a late report is ignored, not an error");
        (await op.GetFromJsonAsync<JobDetail>($"/api/jobs/{created.Job.Id}"))!.Targets[0].Status.Should().Be("Succeeded");
    }

    [Fact]
    public async Task Secrets_in_output_are_redacted_before_they_are_stored()
    {
        var ep = await factory.SeedEndpointAsync(online: true);
        var sid = TestIdentities.NewSid();
        var op = await factory.UserClientAsync(sid, Role.Operator);
        var created = await CreatedAsync(await RunAsync(op, "demo.simulate", [ep], P("hang", 1, 10)));
        var target = (await WaitAsync(op, created.Job.Id, d => d.Targets[0].Status == "Running")).Targets[0];

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IJobReports>().LogAsync(target.Id, "stdout", ["connecting with password=Hunter2! to db", "client_secret: abc123", "normal line"], sid);

        var text = await (await op.GetAsync($"/api/jobs/{created.Job.Id}/targets/{target.Id}/output")).Content.ReadAsStringAsync();
        text.Should().Contain("password=***").And.Contain("client_secret: ***").And.Contain("normal line").And.NotContain("Hunter2").And.NotContain("abc123");
    }

    // ---------- live updates over SignalR

    [Fact]
    public async Task Job_changes_logs_and_the_finish_toast_arrive_over_signalr()
    {
        var ep = await factory.SeedEndpointAsync(online: true);
        var sid = TestIdentities.NewSid();
        var op = await factory.UserClientAsync(sid, Role.Operator);

        // the same headers the browser would carry: antiforgery cookie + token
        using var raw = new HttpClient(factory.Server.CreateHandler()) { BaseAddress = factory.Server.BaseAddress }; // no cookie jar, so we can read Set-Cookie
        raw.DefaultRequestHeaders.Add(TestAuthHandler.SidHeader, sid);
        raw.DefaultRequestHeaders.Add(TestAuthHandler.AccountHeader, "TEST\\user");
        raw.DefaultRequestHeaders.Add(TestAuthHandler.GroupsHeader, TestIdentities.AccessGroupSid);
        var tokenResponse = await raw.GetAsync("/api/antiforgery");
        var token = (await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>())!.Token;
        var cookie = tokenResponse.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        var hub = new HubConnectionBuilder().WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/nexora"), o =>
        {
            o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            o.Transports = HttpTransportType.LongPolling;
            o.Headers[TestAuthHandler.SidHeader] = sid;
            o.Headers[TestAuthHandler.AccountHeader] = "TEST\\user";
            o.Headers[TestAuthHandler.GroupsHeader] = TestIdentities.AccessGroupSid;
            o.Headers["X-XSRF-TOKEN"] = token;
            if (cookie.Length > 0) o.Headers["Cookie"] = cookie;
        }).Build();

        var changed = new List<string>();
        var lines = new List<string>();
        var finished = new TaskCompletionSource<JobSummary>();
        hub.On<JobSummary>("JobChanged", j => { lock (changed) changed.Add(j.Status); });
        hub.On<JobSummary>("JobFinished", j => finished.TrySetResult(j));
        hub.On<System.Text.Json.JsonElement>("JobLog", e => { lock (lines) foreach (var l in e.GetProperty("lines").EnumerateArray()) lines.Add(l.GetProperty("text").GetString()!); });

        await hub.StartAsync();
        try
        {
            var created = await CreatedAsync(await RunAsync(op, "demo.simulate", [ep], P("success", steps: 3, delayMs: 100)));
            (await hub.InvokeAsync<bool>("SubscribeJob", created.Job.Id)).Should().BeTrue();

            var toast = await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));

            toast.Status.Should().Be("Succeeded");
            toast.Id.Should().Be(created.Job.Id);
            lock (changed) changed.Should().Contain("Running").And.Contain("Succeeded");
            lock (lines) lines.Should().Contain("step 3 of 3");

            // someone else's job cannot be subscribed to
            var other = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
            var theirs = await CreatedAsync(await RunAsync(other, "demo.simulate", [ep], P()));
            (await hub.InvokeAsync<bool>("SubscribeJob", theirs.Job.Id)).Should().BeFalse();
        }
        finally { await hub.DisposeAsync(); }
    }

    private sealed class SettingsEnvelope { public PlatformSettings Settings { get; set; } = new(); }
}
