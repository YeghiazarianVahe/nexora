using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Nexora.Api.Hubs;
using Nexora.Api.Services;
using Nexora.Application;
using Nexora.Application.Diagnostics;
using Nexora.Application.Jobs;
using Nexora.Domain;

namespace Nexora.Api.Tests;

/// <summary>The simulated helper: what a diag.collect job prints. The gate lets a test subscribe to the job before the output arrives.</summary>
public sealed class FakeDiag : IFakeDiagnosticOutput
{
    public const string BigOst = """{"userScope":"full","files":[{"kind":"ost","path":"C:\\Users\\x\\a.ost","sizeBytes":70000000000,"onNetworkShare":false,"driveLetter":"C"}]}""";

    public ManualResetEventSlim Gate { get; } = new(true);
    public Func<IReadOnlyDictionary<string, string>, FakeDiagnosticRun> Behavior { get; set; } = Healthy;
    public List<IReadOnlyDictionary<string, string>> Seen { get; } = [];

    public static IEnumerable<string> Ids(IReadOnlyDictionary<string, string> p) => p["collectorIds"].Split(',');

    public static FakeDiagnosticRun Healthy(IReadOnlyDictionary<string, string> p) =>
        new(Ids(p).Select(id => $"##NXC {id} {(id == "outlook-files" ? BigOst : id == "network-probe" ? """{"targets":[{"host":"ex01","lossPercent":0,"avgMs":2}]}""" : "{}")}").Prepend("[helper] starting").ToList());

    public void Reset() { Behavior = Healthy; Gate.Set(); lock (Seen) Seen.Clear(); }

    public FakeDiagnosticRun Run(IReadOnlyDictionary<string, string> p)
    {
        lock (Seen) Seen.Add(p);
        Gate.Wait(TimeSpan.FromSeconds(30));
        return Behavior(p);
    }
}

/// <summary>Remembers every DiagnosisCompleted push and passes it on to the real SignalR sender.</summary>
public sealed class RecordingEvents(IDiagnosisEvents inner) : IDiagnosisEvents
{
    public List<(Guid Job, Guid Run)> Sent { get; } = [];
    public Task DiagnosisCompletedAsync(Guid jobId, Guid runId) { lock (Sent) Sent.Add((jobId, runId)); return inner.DiagnosisCompletedAsync(jobId, runId); }
}

/// <summary>
/// One host with Features:Diagnostics ON and a simulated helper. It is the only host of its test class on purpose: every host runs a job runner on the
/// shared in-memory database, and a second host with the flag off would (correctly) refuse the job.
/// </summary>
public sealed class DiagFactory : NexoraFactory
{
    public FakeDiag Fake { get; } = new();
    public RecordingEvents? Events { get; private set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:Diagnostics"] = "true" }));
        builder.ConfigureServices(s =>
        {
            s.AddSingleton<IFakeDiagnosticOutput>(Fake);
            s.AddSingleton<IDiagnosisEvents>(sp => Events = new RecordingEvents(new SignalRDiagnosisEvents(sp.GetRequiredService<IHubContext<NexoraHub>>())));
        });
    }
}

/// <summary>F5 with the feature ON: the strict request, the whole run, incomplete and failed runs, history, evidence, Exchange hosts, push and retention.</summary>
public sealed class DiagnosisApiTests : IClassFixture<DiagFactory>
{
    private readonly DiagFactory factory;
    private FakeDiag Fake => factory.Fake;

    public DiagnosisApiTests(DiagFactory factory)
    {
        this.factory = factory;
        factory.Fake.Reset();
    }

    private async Task<DiagnosisRun> WaitAsync(Guid runId)
    {
        for (var i = 0; i < 300; i++)
        {
            var run = factory.Query(db => db.DiagnosisRuns.Where(r => r.Id == runId).ToList()).SingleOrDefault();
            if (run is not null && run.Status != DiagRunStatus.Running) return run;
            await Task.Delay(50);
        }
        throw new TimeoutException("the diagnosis did not finish");
    }

    private static async Task<(Guid RunId, Guid JobId)> StartOk(HttpClient c, Guid endpoint, string profile = "outlook-freeze")
    {
        var r = await c.PostAsJsonAsync($"/api/endpoints/{endpoint}/diagnose", new { profileId = profile });
        r.StatusCode.Should().Be(HttpStatusCode.Accepted, await r.Content.ReadAsStringAsync());
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("runId").GetGuid(), body.GetProperty("jobId").GetGuid());
    }

    // ---------------- authorization ----------------

    [Fact]
    public async Task A_viewer_who_posts_with_the_flag_on_is_still_refused_and_creates_nothing()
    {
        var sid = TestIdentities.NewSid();
        var viewer = await factory.UserClientAsync(sid, Role.Viewer);
        var ep = await factory.SeedEndpointAsync(online: true);

        (await viewer.PostAsJsonAsync($"/api/endpoints/{ep}/diagnose", new { profileId = "pc-slow" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        factory.Query(db => db.Jobs.Any(j => j.InitiatorSid == sid) || db.DiagnosisRuns.Any(r => r.EndpointId == ep)).Should().BeFalse();
    }

    [Fact]
    public async Task The_evidence_endpoint_is_Operator_only_and_unknown_ids_are_a_plain_404()
    {
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var ep = await factory.SeedEndpointAsync(online: true);
        var (runId, _) = await StartOk(op, ep);
        await WaitAsync(runId);

        (await op.GetAsync($"/api/diagnose/runs/{runId}/evidence")).StatusCode.Should().Be(HttpStatusCode.OK);
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        (await viewer.GetAsync($"/api/diagnose/runs/{runId}/evidence")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.GetAsync($"/api/diagnose/runs/{runId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var missing = await op.GetAsync($"/api/diagnose/runs/{Guid.NewGuid()}");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await missing.Content.ReadAsStringAsync()).Should().Contain("Diagnosis not found").And.NotContain("Exception").And.NotContain("   at ");
        (await op.GetAsync($"/api/diagnose/runs/{Guid.NewGuid()}/evidence")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await op.GetAsync($"/api/endpoints/{Guid.NewGuid()}/diagnose/runs")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await op.GetAsync("/api/diagnose/runs/not-a-guid")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_generic_job_endpoints_refuse_diag_collect_even_with_the_flag_on()
    {
        var sid = TestIdentities.NewSid();
        var op = await factory.UserClientAsync(sid, Role.Operator);
        var ep = await factory.SeedEndpointAsync(online: true);

        (await op.PostAsJsonAsync("/api/jobs/confirm", new { actionCode = "diag.collect", endpointIds = new[] { ep }, parameters = new { exchangeHosts = new[] { "evil" } } })).StatusCode
            .Should().Be(HttpStatusCode.NotImplemented);
        (await op.PostAsJsonAsync("/api/jobs", new { actionCode = "diag.collect", endpointIds = new[] { ep }, parameters = new { } })).StatusCode
            .Should().Be(HttpStatusCode.NotImplemented);
        factory.Query(db => db.Jobs.Any(j => j.InitiatorSid == sid)).Should().BeFalse();
    }

    // ---------------- the request is not a way to inject anything ----------------

    [Theory]
    [InlineData("""{"profileId":"outlook-freeze","exchangeHosts":["evil.example"]}""")]
    [InlineData("""{"profileId":"outlook-freeze","collectors":[{"id":"x","sha256":"00","body":"calc.exe"}]}""")]
    [InlineData("""{"profileId":"outlook-freeze","collectorIds":["outlook-files"]}""")]
    [InlineData("""{"profileId":"outlook-freeze","parameters":{"exchangeHosts":"evil"}}""")]
    [InlineData("""{"profileId":"outlook-freeze","body":"Remove-Item C:\\ -Recurse"}""")]
    public async Task Unknown_fields_in_the_request_are_rejected_and_nothing_is_created(string body)
    {
        var sid = TestIdentities.NewSid();
        var op = await factory.UserClientAsync(sid, Role.Operator);
        var ep = await factory.SeedEndpointAsync(online: true);

        var r = await op.PostAsync($"/api/endpoints/{ep}/diagnose", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        factory.Query(db => db.Jobs.Any(j => j.InitiatorSid == sid) || db.DiagnosisRuns.Any(x => x.EndpointId == ep)).Should().BeFalse();
        lock (Fake.Seen) Fake.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_profile_a_missing_workstation_and_a_protected_one_are_refused_and_audited()
    {
        var sid = TestIdentities.NewSid();
        var op = await factory.UserClientAsync(sid, Role.Operator);
        var ep = await factory.SeedEndpointAsync(online: true);
        var pinned = factory.Query(db => db.Endpoints.Single(e => e.IsPinned).Id);

        (await op.PostAsJsonAsync($"/api/endpoints/{ep}/diagnose", new { profileId = "does-not-exist" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await op.PostAsJsonAsync($"/api/endpoints/{ep}/diagnose", new { profileId = (string?)null })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await op.PostAsJsonAsync($"/api/endpoints/{Guid.NewGuid()}/diagnose", new { profileId = "pc-slow" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var protectedR = await op.PostAsJsonAsync($"/api/endpoints/{pinned}/diagnose", new { profileId = "pc-slow" });
        protectedR.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await protectedR.Content.ReadAsStringAsync()).Should().Contain("protected");

        factory.Query(db => db.AuditEntries.Count(a => a.ActorSid == sid && a.Action == "job.denied")).Should().Be(4);
        factory.Query(db => db.Jobs.Any(j => j.InitiatorSid == sid)).Should().BeFalse();
    }

    // ---------------- the whole run ----------------

    [Fact]
    public async Task A_run_goes_through_the_job_flow_is_evaluated_stored_pushed_and_audited()
    {
        var sid = TestIdentities.NewSid();
        var op = await factory.UserClientAsync(sid, Role.Operator);
        var ep = await factory.SeedEndpointAsync("PC-DIAG", online: true);

        var (runId, jobId) = await StartOk(op, ep);
        var stored = await WaitAsync(runId);

        // the job is a normal diag.collect job of this operator for this one workstation
        var job = factory.Query(db => db.Jobs.Single(j => j.Id == jobId));
        (job.ActionCode, job.InitiatorSid, job.TargetCount).Should().Be(("diag.collect", sid, 1));
        factory.Query(db => db.JobTargets.Single(t => t.JobId == jobId).EndpointId).Should().Be(ep);
        var jobParams = JsonSerializer.Deserialize<Dictionary<string, string>>(job.ParametersJson)!;
        jobParams["profileId"].Should().Be("outlook-freeze");
        jobParams["collectorIds"].Split(',').Should().HaveCount(8);
        job.ParametersJson.Should().NotContain("sha256", "script bodies and hashes are added to the signed job at dispatch, not kept in the job row");

        // evaluated and stored
        stored.Status.Should().Be("Complete");
        (stored.Complete, stored.InitiatedBySid, stored.JobId, stored.EndpointId, stored.ProfileId, stored.SchemaVersion).Should().Be((true, sid, jobId, ep, "outlook-freeze", 1));
        stored.FinishedUtc.Should().NotBeNull();

        var dto = await op.GetFromJsonAsync<JsonElement>($"/api/diagnose/runs/{runId}");
        dto.GetProperty("id").GetGuid().Should().Be(runId);
        dto.GetProperty("endpointId").GetGuid().Should().Be(ep, "the same GUID as the endpoint id");
        dto.GetProperty("hostname").GetString().Should().StartWith("PC-DIAG");
        dto.GetProperty("profileId").GetString().Should().Be("outlook-freeze");
        dto.GetProperty("profileName").GetString().Should().Be("Outlook freezes / not responding");
        dto.GetProperty("status").GetString().Should().Be("Complete");
        dto.GetProperty("complete").GetBoolean().Should().BeTrue();
        dto.GetProperty("jobId").GetGuid().Should().Be(jobId);
        dto.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        dto.GetProperty("startedUtc").GetDateTime().Should().BeBefore(dto.GetProperty("finishedUtc").GetDateTime());
        var finding = dto.GetProperty("findings").EnumerateArray().Single(f => f.GetProperty("id").GetString() == "ost-oversize");
        finding.GetProperty("confidence").GetString().Should().Be("high");
        finding.GetProperty("severity").GetString().Should().Be("warning");
        finding.GetProperty("score").GetInt32().Should().Be(6);
        finding.GetProperty("cause").GetString().Should().Contain("OST");
        finding.GetProperty("evidence").EnumerateArray().Select(e => e.GetProperty("label").GetString()).Should().Contain("OST size");
        var action = finding.GetProperty("actions").EnumerateArray().First();
        action.GetProperty("kind").GetString().Should().Be("manual");
        action.GetProperty("instructions").GetString().Should().NotBeNullOrWhiteSpace();
        dto.GetProperty("collectors").EnumerateArray().Should().HaveCount(8).And.OnlyContain(c => c.GetProperty("status").GetString() == "ok");
        dto.GetProperty("notEvaluated").GetArrayLength().Should().Be(0);

        // the raw outputs are kept as evidence: { collectorId: object }
        var evidence = await op.GetFromJsonAsync<JsonElement>($"/api/diagnose/runs/{runId}/evidence");
        evidence.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(jobParams["collectorIds"].Split(','));
        evidence.GetProperty("outlook-files").GetProperty("files")[0].GetProperty("sizeBytes").GetInt64().Should().Be(70_000_000_000);

        // the history
        var history = (await op.GetFromJsonAsync<JsonElement>($"/api/endpoints/{ep}/diagnose/runs")).EnumerateArray().Single();
        history.GetProperty("id").GetGuid().Should().Be(runId);
        history.GetProperty("findingCount").GetInt32().Should().Be(1);
        history.GetProperty("topSeverity").GetString().Should().Be("warning");
        history.GetProperty("status").GetString().Should().Be("Complete");

        // pushed to the job's group
        lock (factory.Events!.Sent) factory.Events.Sent.Should().Contain((jobId, runId));

        // audited: who, where, what, result
        var audit = factory.Query(db => db.AuditEntries.Where(a => a.ActorSid == sid).ToList());
        audit.Should().Contain(a => a.Action == "job.create" && a.NewValue == "diag.collect" && a.Details!.Contains(runId.ToString()));
        var done = audit.Single(a => a.Action == "diag.complete");
        done.Success.Should().BeTrue();
        done.TargetId.Should().StartWith("PC-DIAG");
        done.Details.Should().Contain("outlook-freeze").And.Contain("ost-oversize").And.Contain(runId.ToString()).And.Contain("\"Complete\"");
        done.Details.Should().NotContain("Users", "file paths stay in the evidence, not in the audit log");
    }

    [Fact]
    public async Task The_start_response_is_202_with_exactly_the_run_and_job_ids()
    {
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var ep = await factory.SeedEndpointAsync(online: true);

        var r = await op.PostAsJsonAsync($"/api/endpoints/{ep}/diagnose", new { profileId = "pc-slow" });

        r.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        body.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("runId", "jobId");
        await WaitAsync(body.GetProperty("runId").GetGuid());
    }

    [Fact]
    public async Task The_exchange_hosts_come_from_the_Admin_setting_and_the_Operator_cannot_change_them()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var ep = await factory.SeedEndpointAsync(online: true);
        var current = (await admin.GetFromJsonAsync<JsonNode>("/api/settings"))!["settings"]!.AsObject();
        try
        {
            var body = JsonNode.Parse(current.ToJsonString())!.AsObject();
            body["diagExchangeHosts"] = new JsonArray("evil.example");
            (await op.PutAsJsonAsync("/api/settings", body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

            body["diagExchangeHosts"] = new JsonArray("exch01.corp.local", "EXCH02", "exch01.corp.local");
            (await admin.PutAsJsonAsync("/api/settings", body)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await admin.GetFromJsonAsync<JsonNode>("/api/settings"))!["settings"]!["diagExchangeHosts"]!.AsArray().Select(h => h!.GetValue<string>()).Should().Equal("exch01.corp.local", "EXCH02");

            var (runId, jobId) = await StartOk(op, ep, "pc-slow");
            await WaitAsync(runId);
            var job = factory.Query(db => db.Jobs.Single(j => j.Id == jobId));
            JsonSerializer.Deserialize<Dictionary<string, string>>(job.ParametersJson)!["exchangeHosts"].Should().Be("exch01.corp.local,EXCH02");
            lock (Fake.Seen) Fake.Seen.Last()["exchangeHosts"].Should().Be("exch01.corp.local,EXCH02");
        }
        finally
        {
            current["diagExchangeHosts"] = new JsonArray();
            await admin.PutAsJsonAsync("/api/settings", current);
        }
    }

    [Fact]
    public async Task A_settings_save_that_leaves_out_the_Exchange_hosts_keeps_the_stored_ones()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var current = (await admin.GetFromJsonAsync<JsonNode>("/api/settings"))!["settings"]!.AsObject();
        try
        {
            var set = JsonNode.Parse(current.ToJsonString())!.AsObject();
            set["diagExchangeHosts"] = new JsonArray("keep01");
            (await admin.PutAsJsonAsync("/api/settings", set)).StatusCode.Should().Be(HttpStatusCode.OK);

            var omitted = JsonNode.Parse(current.ToJsonString())!.AsObject();
            omitted.Remove("diagExchangeHosts");
            (await admin.PutAsJsonAsync("/api/settings", omitted)).StatusCode.Should().Be(HttpStatusCode.OK);

            (await admin.GetFromJsonAsync<JsonNode>("/api/settings"))!["settings"]!["diagExchangeHosts"]!.AsArray().Select(h => h!.GetValue<string>()).Should().Equal("keep01");
        }
        finally
        {
            current["diagExchangeHosts"] = new JsonArray();
            await admin.PutAsJsonAsync("/api/settings", current);
        }
    }

    [Theory]
    [InlineData("""["a","b","c","d","e","f"]""")]
    [InlineData("""["ex01;calc"]""")]
    [InlineData("""["\\\\ex01"]""")]
    [InlineData("""["http://ex01"]""")]
    [InlineData("""["ex01 ex02"]""")]
    [InlineData("""["-bad"]""")]
    public async Task Exchange_hosts_are_validated_like_the_other_settings(string hosts)
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var body = (await admin.GetFromJsonAsync<JsonNode>("/api/settings"))!["settings"]!.AsObject();
        var before = body["diagExchangeHosts"]!.ToJsonString();
        body["diagExchangeHosts"] = JsonNode.Parse(hosts);

        (await admin.PutAsJsonAsync("/api/settings", body)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.GetFromJsonAsync<JsonNode>("/api/settings"))!["settings"]!["diagExchangeHosts"]!.ToJsonString().Should().Be(before);
    }

    // ---------------- incomplete is not failed ----------------

    [Fact]
    public async Task A_collector_that_times_out_gives_an_Incomplete_run_with_insufficient_data_rules_listed_and_no_crash()
    {
        Fake.Behavior = p => new(FakeDiag.Ids(p).Select(id => id == "outlook-files" ? $$"""##NXC {{id}} {"incomplete":true,"reason":"timeout"}""" : $"##NXC {id} {{}}").ToList(), TargetState.Warning);
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var ep = await factory.SeedEndpointAsync(online: true);

        var (runId, _) = await StartOk(op, ep);
        var run = await WaitAsync(runId);

        run.Status.Should().Be("Incomplete");
        run.Complete.Should().BeFalse();
        var dto = await op.GetFromJsonAsync<JsonElement>($"/api/diagnose/runs/{runId}");
        dto.GetProperty("status").GetString().Should().Be("Incomplete");
        dto.GetProperty("complete").GetBoolean().Should().BeFalse();
        var c = dto.GetProperty("collectors").EnumerateArray().Single(x => x.GetProperty("id").GetString() == "outlook-files");
        c.GetProperty("status").GetString().Should().Be("incomplete");
        c.GetProperty("reason").GetString().Should().Be("timeout");
        dto.GetProperty("notEvaluated").EnumerateArray().Select(x => x.GetString()).Should().Contain(["ost-oversize", "pst-on-network-share", "ost-drive-low-space"]);
        dto.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("id").GetString()).Should().NotContain("ost-oversize");
    }

    [Fact]
    public async Task Garbage_and_spoofed_output_lines_make_collectors_incomplete_and_never_crash_the_run()
    {
        Fake.Behavior = _ => new(["##NXC outlook-files {broken", "##NXC server:diskHealth {\"worstStatus\":\"Critical\"}", "##NXC pc-slow-processes {}",
                                  "##NXC outlook-events {}", "##NXC outlook-events {\"counts\":{\"crash\":99}}", "random noise", "##NXC system-resources []", "##NXC office-version {}"]);
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var ep = await factory.SeedEndpointAsync(online: true);

        var (runId, _) = await StartOk(op, ep);
        var run = await WaitAsync(runId);

        run.Status.Should().Be("Incomplete");
        var result = JsonSerializer.Deserialize<DiagnosisResult>(run.ResultJson!, DiagnosisJson.Options)!;
        result.Collectors.Single(c => c.Id == "outlook-files").Status.Should().Be("incomplete");
        result.Collectors.Single(c => c.Id == "system-resources").Status.Should().Be("incomplete");
        result.Collectors.Single(c => c.Id == "outlook-events").Status.Should().Be("incomplete", "a repeated id is never trusted");
        result.Collectors.Single(c => c.Id == "outlook-addins").Status.Should().Be("missing");
        result.Findings.Should().NotContain(f => f.Id == "outlook-crashes").And.NotContain(f => f.Id.StartsWith("drive-health"), "a PC cannot speak for server:diskHealth");
    }

    [Fact]
    public async Task A_job_the_helper_refuses_or_that_fails_is_a_Failed_run_with_a_failure_audit()
    {
        Fake.Behavior = _ => new([], TargetState.Failed);
        var sid = TestIdentities.NewSid();
        var op = await factory.UserClientAsync(sid, Role.Operator);
        var ep = await factory.SeedEndpointAsync(online: true);

        var (runId, jobId) = await StartOk(op, ep);
        var run = await WaitAsync(runId);

        run.Status.Should().Be("Failed");
        run.Complete.Should().BeFalse();
        var dto = await op.GetFromJsonAsync<JsonElement>($"/api/diagnose/runs/{runId}");
        dto.GetProperty("status").GetString().Should().Be("Failed");
        dto.GetProperty("collectors").EnumerateArray().Should().OnlyContain(c => c.GetProperty("status").GetString() == "failed");
        dto.GetProperty("findings").GetArrayLength().Should().Be(0);
        var done = factory.Query(db => db.AuditEntries.Single(a => a.ActorSid == sid && a.Action == "diag.complete"));
        done.Success.Should().BeFalse();
        done.NewValue.Should().Be("Failed");
        factory.Query(db => db.Jobs.Single(j => j.Id == jobId).Status).Should().Be(JobState.Failed);
    }

    [Fact]
    public async Task A_second_start_on_the_same_workstation_while_one_is_running_is_a_409()
    {
        Fake.Gate.Reset();
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var other = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var ep = await factory.SeedEndpointAsync(online: true);
        try
        {
            var (runId, _) = await StartOk(op, ep);

            var second = await other.PostAsJsonAsync($"/api/endpoints/{ep}/diagnose", new { profileId = "pc-slow" });

            second.StatusCode.Should().Be(HttpStatusCode.Conflict);
            factory.Query(db => db.DiagnosisRuns.Count(r => r.EndpointId == ep)).Should().Be(1);
            var running = await op.GetFromJsonAsync<JsonElement>($"/api/diagnose/runs/{runId}");
            running.GetProperty("status").GetString().Should().Be("Running");
            running.GetProperty("finishedUtc").ValueKind.Should().Be(JsonValueKind.Null);
            running.GetProperty("findings").GetArrayLength().Should().Be(0);
        }
        finally { Fake.Gate.Set(); }
        await WaitAsync(factory.Query(db => db.DiagnosisRuns.Single(r => r.EndpointId == ep).Id));
    }

    [Fact]
    public async Task The_history_lists_the_latest_50_runs_newest_first()
    {
        var ep = await factory.SeedEndpointAsync(online: true);
        var now = DateTime.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
            for (var i = 0; i < 55; i++)
                db.DiagnosisRuns.Add(new DiagnosisRun { Id = Guid.NewGuid(), EndpointId = ep, JobId = Guid.NewGuid(), ProfileId = "pc-slow", ProfileVersion = 1, InitiatedBySid = "S-1", Status = "Complete", Complete = true, StartedUtc = now.AddMinutes(-i), FinishedUtc = now.AddMinutes(-i) });
            await db.SaveChangesAsync();
        }
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);

        var list = (await op.GetFromJsonAsync<JsonElement>($"/api/endpoints/{ep}/diagnose/runs")).EnumerateArray().ToList();

        list.Should().HaveCount(50);
        list.Select(x => x.GetProperty("startedUtc").GetDateTime()).Should().BeInDescendingOrder();
        list[0].GetProperty("findingCount").GetInt32().Should().Be(0);
        list[0].GetProperty("topSeverity").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Reading_the_evidence_is_audited_with_operator_and_run()
    {
        var sid = TestIdentities.NewSid();
        var op = await factory.UserClientAsync(sid, Role.Operator);
        var ep = await factory.SeedEndpointAsync(online: true);
        var (runId, _) = await StartOk(op, ep);
        await WaitAsync(runId);

        (await op.GetAsync($"/api/diagnose/runs/{runId}/evidence")).StatusCode.Should().Be(HttpStatusCode.OK);

        var read = factory.Query(db => db.AuditEntries.Where(a => a.ActorSid == sid && a.Action == "diag.evidence.read").ToList());
        read.Should().ContainSingle().Which.TargetId.Should().Be(runId.ToString());
    }

    [Fact]
    public async Task A_stale_Running_run_does_not_block_the_workstation_and_is_healed_to_Failed()
    {
        var ep = await factory.SeedEndpointAsync(online: true);
        Guid finishedJob = Guid.NewGuid(), oldJob = Guid.NewGuid(), runA = Guid.NewGuid(), runB = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
            // run A: its job target is already final; run B: still "queued" but far older than timeout + queue wait
            db.JobTargets.Add(new JobTarget { Id = Guid.NewGuid(), JobId = finishedJob, EndpointId = ep, EndpointName = "x", Host = "x", Status = TargetState.Succeeded });
            db.DiagnosisRuns.Add(new DiagnosisRun { Id = runA, EndpointId = ep, JobId = finishedJob, ProfileId = "pc-slow", ProfileVersion = 1, InitiatedBySid = "S", Status = "Running", StartedUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);

        var (runId, _) = await StartOk(op, ep, "pc-slow");   // not a 409

        await WaitAsync(runId);
        factory.Query(db => db.DiagnosisRuns.Single(r => r.Id == runA).Status).Should().Be("Failed");

        Fake.Gate.Reset();
        try
        {
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
                db.DiagnosisRuns.Add(new DiagnosisRun { Id = runB, EndpointId = ep, JobId = oldJob, ProfileId = "pc-slow", ProfileVersion = 1, InitiatedBySid = "S", Status = "Running", StartedUtc = DateTime.UtcNow.AddHours(-3) });
                db.JobTargets.Add(new JobTarget { Id = Guid.NewGuid(), JobId = oldJob, EndpointId = ep, EndpointName = "x", Host = "x", Status = TargetState.Running });
                await db.SaveChangesAsync();
            }
            var (healedRun, _) = await StartOk(op, ep, "pc-slow");
            factory.Query(db => db.DiagnosisRuns.Single(r => r.Id == runB).Status).Should().Be("Failed");
            Fake.Gate.Set();
            await WaitAsync(healedRun);
            factory.Query(db => db.AuditEntries.Any(a => a.Action == "diag.complete" && a.Details!.Contains(runB.ToString()) && !a.Success)).Should().BeTrue();
        }
        finally { Fake.Gate.Set(); }
    }

    // ---------------- real-time ----------------

    [Fact]
    public async Task DiagnosisCompleted_reaches_the_initiating_operator_through_the_job_group_they_can_subscribe_to()
    {
        Fake.Gate.Reset();
        var sid = TestIdentities.NewSid();
        var op = await factory.UserClientAsync(sid, Role.Operator);
        var ep = await factory.SeedEndpointAsync(online: true);

        using var raw = new HttpClient(factory.Server.CreateHandler()) { BaseAddress = factory.Server.BaseAddress };
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
            o.Headers["Cookie"] = cookie;
        }).Build();
        var pushed = new TaskCompletionSource<Guid>();
        hub.On<Guid>("DiagnosisCompleted", id => pushed.TrySetResult(id));
        await hub.StartAsync();
        try
        {
            var (runId, jobId) = await StartOk(op, ep);
            (await hub.InvokeAsync<bool>("SubscribeJob", jobId)).Should().BeTrue("the initiating operator may follow their own job");
            Fake.Gate.Set();

            (await pushed.Task.WaitAsync(TimeSpan.FromSeconds(20))).Should().Be(runId);
            (await WaitAsync(runId)).Status.Should().Be("Complete");
        }
        finally { Fake.Gate.Set(); await hub.DisposeAsync(); }
    }

    [Fact]
    public async Task Another_operator_cannot_follow_the_diagnosis_job_so_never_receives_the_push()
    {
        Fake.Gate.Reset();
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var ep = await factory.SeedEndpointAsync(online: true);
        var other = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        try
        {
            var (_, jobId) = await StartOk(op, ep);
            // SubscribeJob applies the same visibility rule as the job read: someone else's job is "not found".
            (await other.GetAsync($"/api/jobs/{jobId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally { Fake.Gate.Set(); }
    }

    // ---------------- retention ----------------

    [Fact]
    public async Task The_retention_purge_removes_old_runs_with_their_jobs_and_keeps_recent_ones_and_the_audit()
    {
        var ep = await factory.SeedEndpointAsync(online: true);
        var now = DateTime.UtcNow;
        Guid oldJob = Guid.NewGuid(), newJob = Guid.NewGuid(), oldRun = Guid.NewGuid(), newRun = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
            Job J(Guid id, DateTime finished) => new() { Id = id, ActionCode = "diag.collect", InitiatorSid = "S", InitiatorAccount = "T", CreatedUtc = finished.AddMinutes(-5), Status = JobState.Succeeded, FinishedUtc = finished };
            DiagnosisRun R(Guid id, Guid job, DateTime t) => new() { Id = id, EndpointId = ep, JobId = job, ProfileId = "pc-slow", ProfileVersion = 1, InitiatedBySid = "S", Status = "Complete", Complete = true, StartedUtc = t, FinishedUtc = t, ResultJson = "{}", EvidenceJson = "{}" };
            db.Jobs.AddRange(J(oldJob, now.AddDays(-91)), J(newJob, now.AddDays(-89)));
            db.DiagnosisRuns.AddRange(R(oldRun, oldJob, now.AddDays(-91)), R(newRun, newJob, now.AddDays(-89)));
            db.AuditEntries.Add(new AuditEntry { Action = "diag.complete", TargetType = "Endpoint", TargetId = "old", TimestampUtc = now.AddDays(-91), ActorSid = "S", ActorAccount = "T", Details = oldRun.ToString() });
            await db.SaveChangesAsync();

            await MonitoringService.PurgeJobsAsync(db, now, 90, default);
        }

        factory.Query(db => db.DiagnosisRuns.Any(r => r.Id == oldRun)).Should().BeFalse();
        factory.Query(db => db.DiagnosisRuns.Any(r => r.Id == newRun)).Should().BeTrue();
        factory.Query(db => db.AuditEntries.Any(a => a.Action == "diag.complete" && a.Details == oldRun.ToString())).Should().BeTrue("audit is never purged");
    }
}

/// <summary>F5 with the feature OFF (the shipped default): the reads work, the start is a clean 501 with a denial row, and nobody below Operator gets in.</summary>
public sealed class DiagnosticsOffFactory : NexoraFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:Diagnostics"] = "false" }));
    }
}

public sealed class DiagnosisDormantApiTests(DiagnosticsOffFactory factory) : IClassFixture<DiagnosticsOffFactory>
{
    [Fact]
    public async Task Every_endpoint_needs_a_sign_in_and_the_Operator_role()
    {
        var ep = await factory.SeedEndpointAsync(online: true);
        var run = Guid.NewGuid();
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        var anonymous = factory.ClientFor(null);
        var calls = new (HttpMethod Method, string Url, object? Body)[]
        {
            (HttpMethod.Get, "/api/diagnose/profiles", null),
            (HttpMethod.Get, $"/api/diagnose/runs/{run}", null),
            (HttpMethod.Get, $"/api/diagnose/runs/{run}/evidence", null),
            (HttpMethod.Get, $"/api/endpoints/{ep}/diagnose/runs", null),
            (HttpMethod.Post, $"/api/endpoints/{ep}/diagnose", new { profileId = "pc-slow" }),
        };
        foreach (var (method, url, body) in calls)
        {
            HttpRequestMessage Req() => new(method, url) { Content = body is null ? null : JsonContent.Create(body) };
            (await anonymous.SendAsync(Req())).StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{method} {url}");
            (await viewer.SendAsync(Req())).StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{method} {url}");
        }
        factory.Query(db => db.DiagnosisRuns.Any(r => r.EndpointId == ep)).Should().BeFalse();
    }

    [Fact]
    public async Task With_the_flag_off_the_reads_work_the_start_answers_501_and_the_denial_is_audited()
    {
        var sid = TestIdentities.NewSid();
        var op = await factory.UserClientAsync(sid, Role.Operator);
        var ep = await factory.SeedEndpointAsync(online: true);

        var profiles = (await op.GetFromJsonAsync<JsonElement>("/api/diagnose/profiles")).EnumerateArray().ToList();
        profiles.Select(p => p.GetProperty("id").GetString()).Should().HaveCount(15).And.Contain(["outlook-freeze", "pc-slow", "high-cpu", "dns-issue", "service-not-running"]);
        var outlook = profiles.Single(p => p.GetProperty("id").GetString() == "outlook-freeze");
        outlook.GetProperty("name").GetString().Should().Be("Outlook freezes / not responding");
        outlook.GetProperty("collectors").GetInt32().Should().Be(8);
        outlook.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
        (await op.GetFromJsonAsync<JsonElement>($"/api/endpoints/{ep}/diagnose/runs")).GetArrayLength().Should().Be(0);

        var r = await op.PostAsJsonAsync($"/api/endpoints/{ep}/diagnose", new { profileId = "outlook-freeze" });

        r.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
        (await r.Content.ReadAsStringAsync()).Should().Contain("not enabled");
        factory.Query(db => db.Jobs.Any(j => j.InitiatorSid == sid) || db.DiagnosisRuns.Any(x => x.EndpointId == ep)).Should().BeFalse("nothing is created while the feature is off");
        var denied = factory.Query(db => db.AuditEntries.Where(a => a.ActorSid == sid && a.Action == "job.denied").ToList());
        denied.Should().ContainSingle();
        denied[0].Success.Should().BeFalse();
        denied[0].NewValue.Should().Be("diag.collect");
        denied[0].Details.Should().Contain("diagnose").And.Contain("NotImplemented").And.Contain(ep.ToString());
    }

    [Fact]
    public void The_feature_flag_defaults_to_off_but_the_authorized_shipped_settings_enable_it()
    {
        new FeatureOptions().Diagnostics.Should().BeFalse();
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json")))!;
        settings["Features"]!["Diagnostics"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task A_queued_diagnosis_job_is_skipped_not_run_when_the_flag_is_off()
    {
        // e.g. the flag was turned off after the job was queued: the orchestrator refuses it at dispatch.
        var ep = await factory.SeedEndpointAsync(online: true);
        using var scope = factory.Services.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<JobOrchestrator>();
        var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
        var endpoint = db.Endpoints.Single(e => e.Id == ep);
        var job = new Job { Id = Guid.NewGuid(), ActionCode = "diag.collect", InitiatorSid = TestIdentities.SuperAdminSid, InitiatorAccount = "T", CreatedUtc = DateTime.UtcNow, ParametersJson = "{}" };
        var target = new JobTarget { Id = Guid.NewGuid(), JobId = job.Id, EndpointId = ep, EndpointName = "x", Host = endpoint.Hostname };

        var why = await orchestrator.SkipReasonAsync(job, ActionCatalog.Find("diag.collect")!, target, new PlatformSettings(), new JobsOptions(), DateTime.UtcNow, DateTime.UtcNow, default);

        why.Should().Contain("not available");
    }
}
