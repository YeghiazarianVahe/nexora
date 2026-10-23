using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexora.Api.Hubs;
using Nexora.Application;
using Nexora.Application.Jobs;
using Nexora.Application.Services;
using Nexora.Contracts;
using Nexora.Domain;

namespace Nexora.Api.Tests;

/// <summary>F2 (Processes and services): the Services view API, protected services, dependents and the risk tiers. Fakes only: no PC, no WMI.</summary>
public sealed class ServiceControlApiTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    // ---------- helpers

    /// <summary>Settings that are not shared with other tests: the Admin list is fixed per host.</summary>
    private sealed class FixedSettings(params string[] adminList) : ISettingsService
    {
        public Task<PlatformSettings> GetAsync(CancellationToken ct = default) => Task.FromResult(new PlatformSettings { ProtectedHosts = [], ProtectedServices = [.. adminList] });
        public Task<Result<PlatformSettings>> UpdateAsync(Actor actor, PlatformSettings settings, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private WebApplicationFactory<Program> WithAdminList(params string[] list) =>
        factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<ISettingsService>(new FixedSettings(list)))));

    private static HttpClient Client(WebApplicationFactory<Program> f, string sid)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add(TestAuthHandler.SidHeader, sid);
        c.DefaultRequestHeaders.Add(TestAuthHandler.AccountHeader, "TEST\\user");
        c.DefaultRequestHeaders.Add(TestAuthHandler.GroupsHeader, TestIdentities.AccessGroupSid);
        return c;
    }

    private async Task<(HttpClient Client, string Sid)> UserOnAsync(WebApplicationFactory<Program> f, Role role)
    {
        var sid = TestIdentities.NewSid();
        await factory.SeedUserAsync(sid, role, "TEST\\" + role);
        return (await NexoraFactory.WithTokenAsync(Client(f, sid)), sid);
    }

    private async Task<(Guid Id, string Host)> OnlinePcAsync()
    {
        var id = await factory.SeedEndpointAsync("PC-SVC", online: true);
        return (id, factory.Query(db => db.Endpoints.Single(e => e.Id == id).Hostname));
    }

    private static Task<HttpResponseMessage> Confirm(HttpClient c, string code, Guid ep, object p, string? reason = null, string? typed = null) =>
        c.PostAsJsonAsync("/api/jobs/confirm", new { actionCode = code, endpointIds = new[] { ep }, parameters = p, reason, typed });

    private static Task<HttpResponseMessage> Create(HttpClient c, string code, Guid ep, object p, string? reason = null, string? token = null) =>
        c.PostAsJsonAsync("/api/jobs", new { actionCode = code, endpointIds = new[] { ep }, parameters = p, reason, confirmToken = token });

    private static object Svc(string name, bool? stopDependents = null) =>
        stopDependents is { } b ? new { serviceName = name, stopDependents = b } : new { serviceName = name };

    private static async Task<JsonElement> JsonOf(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<JsonElement>());

    private List<AuditEntry> Denied(string sid) =>
        factory.Query(db => db.AuditEntries.Where(a => a.Action == "job.denied" && a.ActorSid == sid).OrderBy(a => a.Id).ToList());

    private static string[] Allowed(JsonElement row) => row.GetProperty("allowed").EnumerateArray().Select(x => x.GetString()!).ToArray();

    // ---------- authorization: the Services API is Operator and above

    [Fact]
    public async Task A_viewer_gets_403_on_the_services_and_dependents_endpoints_and_nothing_is_read()
    {
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        var (id, host) = await OnlinePcAsync();
        var before = FakeServiceCollector.Calls;

        (await viewer.GetAsync($"/api/endpoints/{id}/services")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.GetAsync($"/api/endpoints/{id}/services/Spooler/dependents")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        FakeServiceCollector.Calls.Should().Be(before, "the policy stops the request before any WMI read");
        host.Should().NotBeNull();
    }

    [Fact]
    public async Task Anonymous_and_role_less_callers_cannot_read_services()
    {
        var (id, _) = await OnlinePcAsync();

        (await factory.ClientFor(null).GetAsync($"/api/endpoints/{id}/services")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var none = await factory.UserClientAsync(TestIdentities.NewSid(), role: null);
        (await none.GetAsync($"/api/endpoints/{id}/services")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("svc.start")]
    [InlineData("svc.stop")]
    [InlineData("svc.restart")]
    [InlineData("proc.kill")]
    public async Task A_viewer_cannot_confirm_or_create_service_or_process_jobs(string code)
    {
        var (client, sid) = await UserOnAsync(factory, Role.Viewer);
        var (id, _) = await OnlinePcAsync();
        object p = code == "proc.kill" ? new { pid = 4242, processName = "notepad.exe" } : Svc("Spooler");

        (await Confirm(client, code, id, p, "r", "x")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Create(client, code, id, p, "r", "garbage")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        factory.Query(db => db.Jobs.Any(j => j.InitiatorSid == sid)).Should().BeFalse();
        Denied(sid).Should().HaveCount(2, "the refusals are audited");
    }

    [Fact]
    public async Task The_services_controller_exposes_only_GET()
    {
        var t = typeof(Nexora.Api.Controllers.WorkstationServicesController);
        t.GetCustomAttribute<AuthorizeAttribute>()!.Policy.Should().Be(Nexora.Api.Auth.Policies.Operator);
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            m.GetCustomAttributes().OfType<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>().Should().OnlyContain(a => a.HttpMethods.All(x => x == "GET"), m.Name);
        await Task.CompletedTask;
    }

    // ---------- the list

    [Fact]
    public async Task The_service_list_carries_state_start_mode_protection_and_the_allowed_actions()
    {
        var (op, _) = await UserOnAsync(factory, Role.Operator);
        var (id, host) = await OnlinePcAsync();
        FakeServiceCollector.ServicesByHost[host] = FakeServiceCollector.Default();

        var r = await op.GetAsync($"/api/endpoints/{id}/services");

        r.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = (await JsonOf(r)).EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!);
        rows["Spooler"].GetProperty("displayName").GetString().Should().Be("Print Spooler");
        rows["Spooler"].GetProperty("state").GetString().Should().Be("Running");
        rows["Spooler"].GetProperty("startMode").GetString().Should().Be("Auto");
        rows["Spooler"].GetProperty("protected").GetBoolean().Should().BeFalse();
        Allowed(rows["Spooler"]).Should().Equal("stop", "restart");
        Allowed(rows["BITS"]).Should().Equal("start");
        Allowed(rows["Fax"]).Should().BeEmpty("disabled services cannot be started");
        rows["wuauserv"].GetProperty("state").GetString().Should().Be("StartPending");
        Allowed(rows["wuauserv"]).Should().Equal("stop");
        rows["RpcSs"].GetProperty("protected").GetBoolean().Should().BeTrue();
        Allowed(rows["RpcSs"]).Should().BeEmpty();
        Allowed(rows["Dhcp"]).Should().Equal("stop", "restart");
    }

    [Fact]
    public async Task A_service_on_the_admin_list_is_protected_for_stop_and_restart_only()
    {
        using var f = WithAdminList("BITS", "Spooler");
        var (op, _) = await UserOnAsync(f, Role.Operator);
        var (id, host) = await OnlinePcAsync();
        FakeServiceCollector.ServicesByHost[host] = FakeServiceCollector.Default();

        var rows = (await JsonOf(await op.GetAsync($"/api/endpoints/{id}/services"))).EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!);

        rows["Spooler"].GetProperty("protected").GetBoolean().Should().BeTrue();
        Allowed(rows["Spooler"]).Should().BeEmpty();
        rows["BITS"].GetProperty("protected").GetBoolean().Should().BeTrue();
        Allowed(rows["BITS"]).Should().Equal("start");
    }

    [Fact]
    public async Task An_unknown_workstation_is_404_and_an_offline_one_is_a_typed_200_envelope()
    {
        var (op, _) = await UserOnAsync(factory, Role.Operator);
        var offline = await factory.SeedEndpointAsync("PC-SVC-OFF", online: false);

        (await op.GetAsync($"/api/endpoints/{Guid.NewGuid()}/services")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var r = await op.GetAsync($"/api/endpoints/{offline}/services");
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await JsonOf(r);
        body.GetProperty("unavailable").GetBoolean().Should().BeTrue();
        body.GetProperty("reason").GetString().Should().Be("The workstation is offline.");
        body.GetProperty("items").GetArrayLength().Should().Be(0);

        var dep = await op.GetAsync($"/api/endpoints/{offline}/services/Spooler/dependents");
        dep.StatusCode.Should().Be(HttpStatusCode.OK);
        var depBody = await JsonOf(dep);
        depBody.GetProperty("unavailable").GetBoolean().Should().BeTrue();
        depBody.GetProperty("effectiveRisk").GetString().Should().Be("High", "unknown dependents are treated as running ones");
    }

    [Fact]
    public async Task A_read_failure_is_a_typed_200_envelope_with_a_generic_reason_and_never_the_exception_text()
    {
        var (op, _) = await UserOnAsync(factory, Role.Operator);
        var (id, host) = await OnlinePcAsync();
        FakeServiceCollector.FailingHosts[host] = true;

        var list = await op.GetAsync($"/api/endpoints/{id}/services");
        var dep = await op.GetAsync($"/api/endpoints/{id}/services/Spooler/dependents");

        foreach (var r in new[] { list, dep })
        {
            r.StatusCode.Should().Be(HttpStatusCode.OK);
            var text = await r.Content.ReadAsStringAsync();
            text.Should().Contain("\"unavailable\":true").And.NotContain("10.99.99.99").And.NotContain("secret-detail").And.NotContain("InvalidOperation");
        }
    }

    [Fact]
    public async Task An_offline_workstation_shows_the_last_known_services_with_every_action_switched_off()
    {
        var (op, _) = await UserOnAsync(factory, Role.Operator);
        var (id, host) = await OnlinePcAsync();
        FakeServiceCollector.ServicesByHost[host] = FakeServiceCollector.Default();
        (await op.GetAsync($"/api/endpoints/{id}/services")).StatusCode.Should().Be(HttpStatusCode.OK);   // remembered
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
            db.Endpoints.Single(e => e.Id == id).Online = false;
            await db.SaveChangesAsync();
        }

        var body = await JsonOf(await op.GetAsync($"/api/endpoints/{id}/services"));

        body.GetProperty("unavailable").GetBoolean().Should().BeTrue();
        var items = body.GetProperty("items").EnumerateArray().ToList();
        items.Should().NotBeEmpty();
        items.Should().OnlyContain(i => i.GetProperty("allowed").GetArrayLength() == 0);
        body.GetProperty("lastKnownUtc").ValueKind.Should().Be(JsonValueKind.String);
    }

    // ---------- dependents

    [Fact]
    public async Task Dependents_report_running_all_protection_and_the_effective_risk()
    {
        var (op, _) = await UserOnAsync(factory, Role.Operator);
        var (id, host) = await OnlinePcAsync();
        FakeServiceCollector.DependentsByKey[host + "|Spooler"] = [new("FaxSvc", "Running"), new("Idle", "Stopped"), new("WinDefend", "Stopped")];

        var body = await JsonOf(await op.GetAsync($"/api/endpoints/{id}/services/Spooler/dependents"));

        body.GetProperty("running").EnumerateArray().Select(x => x.GetString()).Should().Equal("FaxSvc");
        body.GetProperty("all").EnumerateArray().Select(x => x.GetString()).Should().Equal("FaxSvc", "Idle", "WinDefend");
        body.GetProperty("anyProtected").GetBoolean().Should().BeTrue();
        body.GetProperty("effectiveRisk").GetString().Should().Be("High");
        body.TryGetProperty("unavailable", out _).Should().BeFalse();

        var (id2, _) = await OnlinePcAsync();
        var none = await JsonOf(await op.GetAsync($"/api/endpoints/{id2}/services/Spooler/dependents"));
        none.GetProperty("running").GetArrayLength().Should().Be(0);
        none.GetProperty("anyProtected").GetBoolean().Should().BeFalse();
        none.GetProperty("effectiveRisk").GetString().Should().Be("Medium");
    }

    [Theory]
    [InlineData("a%3Bb")]
    [InlineData("a%27b")]
    [InlineData("a%5Cb")]
    [InlineData("a%0A")]
    public async Task A_service_name_that_does_not_match_the_pattern_is_refused_before_any_read(string encoded)
    {
        var (op, _) = await UserOnAsync(factory, Role.Operator);
        var (id, _) = await OnlinePcAsync();
        var before = FakeServiceCollector.Calls;

        (await op.GetAsync($"/api/endpoints/{id}/services/{encoded}/dependents")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        FakeServiceCollector.Calls.Should().Be(before);
    }

    // ---------- protected services are refused server-side, for every role

    [Theory]
    [InlineData(Role.Operator, "svc.stop", "RpcSs")]
    [InlineData(Role.Operator, "svc.restart", "rpcss")]
    [InlineData(Role.Operator, "svc.stop", "  WinDefend  ")]
    [InlineData(Role.Operator, "svc.restart", "NETLOGON")]
    [InlineData(Role.Admin, "svc.stop", "RpcSs")]
    [InlineData(Role.Admin, "svc.restart", "windefend")]
    [InlineData(Role.Admin, "svc.stop", "  lsm ")]
    public async Task A_floor_service_cannot_be_stopped_or_restarted_by_an_operator_or_an_admin(Role role, string code, string name)
    {
        var (client, sid) = await UserOnAsync(factory, role);
        var (id, host) = await OnlinePcAsync();

        var confirm = await Confirm(client, code, id, Svc(name), "because", host);
        var create = await Create(client, code, id, Svc(name), "because", "anything");

        confirm.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await confirm.Content.ReadAsStringAsync()).Should().Contain("protected");
        create.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        factory.Query(db => db.Jobs.Any(j => j.InitiatorSid == sid)).Should().BeFalse();
        var rows = Denied(sid);
        rows.Should().HaveCount(2).And.OnlyContain(r => !r.Success && r.NewValue == code && r.ActorSid == sid);
        rows.Select(r => r.Details).Should().OnlyContain(d => d!.Contains("protected"));
    }

    [Theory]
    [InlineData(Role.Operator, "svc.stop", "MyCorpAgent")]
    [InlineData(Role.Operator, "svc.restart", "mycorpagent")]
    [InlineData(Role.Operator, "svc.stop", "  MYCORPAGENT ")]
    [InlineData(Role.Admin, "svc.stop", "MyCorpAgent")]
    [InlineData(Role.Admin, "svc.restart", "MyCorpAgent ")]
    public async Task A_service_on_the_admin_list_cannot_be_stopped_or_restarted_by_an_operator_or_an_admin(Role role, string code, string name)
    {
        using var f = WithAdminList("MyCorpAgent");
        var (client, sid) = await UserOnAsync(f, role);
        var (id, host) = await OnlinePcAsync();

        var confirm = await Confirm(client, code, id, Svc(name), "because", host);
        var create = await Create(client, code, id, Svc(name), "because", "anything");

        confirm.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await confirm.Content.ReadAsStringAsync()).Should().Contain("protected service");
        create.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        factory.Query(db => db.Jobs.Any(j => j.InitiatorSid == sid)).Should().BeFalse();
        Denied(sid).Should().HaveCount(2).And.OnlyContain(r => !r.Success);
    }

    [Fact]
    public async Task Starting_a_service_on_the_admin_list_is_allowed()
    {
        using var f = WithAdminList("MyCorpAgent");
        var (client, sid) = await UserOnAsync(f, Role.Operator);
        var (id, _) = await OnlinePcAsync();

        var r = await Create(client, "svc.start", id, Svc("MyCorpAgent"));

        r.StatusCode.Should().Be(HttpStatusCode.Accepted, await r.Content.ReadAsStringAsync());
        Denied(sid).Should().BeEmpty();
    }

    [Fact]
    public async Task Preflight_refuses_a_protected_service_and_audits_it()
    {
        using var f = WithAdminList("MyCorpAgent");
        var (client, sid) = await UserOnAsync(f, Role.Operator);
        var (id, _) = await OnlinePcAsync();

        var r = await client.PostAsJsonAsync("/api/jobs/preflight", new { actionCode = "svc.stop", endpointIds = new[] { id }, parameters = Svc("mycorpagent") });

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Denied(sid).Should().ContainSingle().Which.Details.Should().Contain("\"stage\":\"preflight\"").And.Contain("protected service");
    }

    // ---------- risk tiers and the typed confirmation

    [Theory]
    [InlineData("svc.stop")]
    [InlineData("svc.restart")]
    public async Task Running_dependents_make_a_stop_or_restart_high_with_reason_and_typed_name_and_the_audit_says_so(string code)
    {
        var (op, sid) = await UserOnAsync(factory, Role.Operator);
        var (id, host) = await OnlinePcAsync();
        FakeServiceCollector.DependentsByKey[host + "|Spooler"] = [new("FaxSvc", "Running")];

        (await Confirm(op, code, id, Svc("Spooler"))).StatusCode.Should().Be(HttpStatusCode.BadRequest, "High needs a reason");
        var noTyped = await Confirm(op, code, id, Svc("Spooler"), "dependents are fine");
        noTyped.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await noTyped.Content.ReadAsStringAsync()).Should().Contain("Type the workstation name");
        (await Confirm(op, code, id, Svc("Spooler"), "dependents are fine", "someone-else")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var ok = await Confirm(op, code, id, Svc("Spooler"), "dependents are fine", host);
        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        var token = (await ok.Content.ReadFromJsonAsync<ConfirmResponse>())!.Token;
        (await Create(op, code, id, Svc("Spooler"), null, token)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "the reason is part of what was confirmed");
        var created = await Create(op, code, id, Svc("Spooler"), "dependents are fine", token);
        created.StatusCode.Should().Be(HttpStatusCode.Accepted, await created.Content.ReadAsStringAsync());

        var audit = factory.Query(db => db.AuditEntries.Single(a => a.ActorSid == sid && a.Action == "job.create"));
        audit.Success.Should().BeTrue();
        audit.Details.Should().Contain("\"effectiveRisk\":\"High\"").And.Contain("serviceName");
        Denied(sid).Should().HaveCount(4, "each refused confirm/create above left a denial row");
    }

    [Theory]
    [InlineData("svc.stop")]
    [InlineData("svc.restart")]
    public async Task No_running_dependents_keeps_medium_so_no_reason_or_typed_name_is_needed(string code)
    {
        var (op, _) = await UserOnAsync(factory, Role.Operator);
        var (id, host) = await OnlinePcAsync();
        FakeServiceCollector.DependentsByKey[host + "|Spooler"] = [new("Idle", "Stopped")];

        var confirm = await Confirm(op, code, id, Svc("Spooler"));

        confirm.StatusCode.Should().Be(HttpStatusCode.OK, await confirm.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_failed_dependents_lookup_makes_the_request_high()
    {
        var (op, _) = await UserOnAsync(factory, Role.Operator);
        var (id, host) = await OnlinePcAsync();
        FakeServiceCollector.FailingHosts[host] = true;

        (await Confirm(op, "svc.stop", id, Svc("Spooler"))).StatusCode.Should().Be(HttpStatusCode.BadRequest, "fail safe: a reason is required");
        (await Confirm(op, "svc.stop", id, Svc("Spooler"), "lookup failed, I know", host)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Start_is_low_and_needs_no_confirmation_token()
    {
        var (op, sid) = await UserOnAsync(factory, Role.Operator);
        var (id, host) = await OnlinePcAsync();
        FakeServiceCollector.DependentsByKey[host + "|Spooler"] = [new("FaxSvc", "Running")];

        var r = await Create(op, "svc.start", id, Svc("Spooler"));

        r.StatusCode.Should().Be(HttpStatusCode.Accepted, await r.Content.ReadAsStringAsync());
        factory.Query(db => db.AuditEntries.Single(a => a.ActorSid == sid && a.Action == "job.create")).Details.Should().Contain("\"effectiveRisk\":\"Low\"");
    }

    [Fact]
    public async Task Preflight_shows_the_escalated_risk_when_parameters_are_given()
    {
        var (op, _) = await UserOnAsync(factory, Role.Operator);
        var (id, host) = await OnlinePcAsync();
        FakeServiceCollector.DependentsByKey[host + "|Spooler"] = [new("FaxSvc", "Running")];

        async Task<PreflightResult> Pre(string code, object? p) =>
            (await (await op.PostAsJsonAsync("/api/jobs/preflight", new { actionCode = code, endpointIds = new[] { id }, parameters = p })).Content.ReadFromJsonAsync<PreflightResult>())!;

        (await Pre("svc.stop", Svc("Spooler"))).EffectiveRisk.Should().Be("High");
        (await Pre("svc.restart", Svc("Spooler"))).EffectiveRisk.Should().Be("High");
        (await Pre("svc.stop", null)).EffectiveRisk.Should().Be("Medium", "without parameters there is nothing to look up");
        (await Pre("svc.start", Svc("Spooler"))).EffectiveRisk.Should().Be("Low");
        (await Pre("svc.stop", Svc("Other"))).EffectiveRisk.Should().Be("Medium");
    }

    // ---------- stop dependents

    [Fact]
    public async Task Stop_dependents_is_refused_when_a_dependent_is_on_the_admin_list_or_the_floor_and_when_the_lookup_fails()
    {
        using var f = WithAdminList("MyCorpAgent");
        var (op, sid) = await UserOnAsync(f, Role.Operator);
        var (listed, listedHost) = await OnlinePcAsync();
        var (floor, floorHost) = await OnlinePcAsync();
        var (failing, failingHost) = await OnlinePcAsync();
        FakeServiceCollector.DependentsByKey[listedHost + "|Spooler"] = [new("MyCorpAgent", "Running")];
        FakeServiceCollector.DependentsByKey[floorHost + "|Spooler"] = [new("BFE", "Running")];
        FakeServiceCollector.FailingHosts[failingHost] = true;

        var a = await Confirm(op, "svc.stop", listed, Svc("Spooler", true), "r", listedHost);
        var b = await Confirm(op, "svc.restart", floor, Svc("Spooler", true), "r", floorHost);
        var c = await Confirm(op, "svc.stop", failing, Svc("Spooler", true), "r", failingHost);

        a.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await a.Content.ReadAsStringAsync()).Should().Contain("MyCorpAgent").And.Contain("protected");
        b.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await b.Content.ReadAsStringAsync()).Should().Contain("BFE");
        c.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await c.Content.ReadAsStringAsync()).Should().Contain("could not be checked");
        Denied(sid).Should().HaveCount(3);
    }

    [Fact]
    public async Task Stop_dependents_goes_ahead_when_the_dependents_are_ordinary()
    {
        var (op, _) = await UserOnAsync(factory, Role.Operator);
        var (id, host) = await OnlinePcAsync();
        FakeServiceCollector.DependentsByKey[host + "|Spooler"] = [new("FaxSvc", "Running")];

        (await Confirm(op, "svc.stop", id, Svc("Spooler", true), "r", host)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---------- the "name" alias used by the Services view

    [Fact]
    public async Task The_services_view_can_send_name_instead_of_serviceName_but_not_both_and_the_job_stays_in_the_catalog_shape()
    {
        var (op, sid) = await UserOnAsync(factory, Role.Operator);
        var (id, _) = await OnlinePcAsync();

        (await Confirm(op, "svc.stop", id, new { name = "Spooler", stopDependents = false })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Confirm(op, "svc.stop", id, new { name = "Spooler", serviceName = "Spooler" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Confirm(op, "svc.stop", id, new { name = "RpcSs" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Confirm(op, "proc.kill", id, new { name = "x" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var created = await Create(op, "svc.start", id, new { name = "Spooler" });
        created.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await created.Content.ReadFromJsonAsync<JobDetail>())!.ParametersJson.Should().Contain("serviceName").And.NotContain("\"name\"");
        sid.Should().NotBeNull();
    }

    // ---------- audit rows for success, failure and denial

    [Fact]
    public async Task A_service_job_leaves_audit_rows_for_success_failure_and_denial_under_the_operators_own_identity()
    {
        var (op, sid) = await UserOnAsync(factory, Role.Operator);
        var (id, host) = await OnlinePcAsync();

        var denied = await Confirm(op, "svc.stop", id, Svc("RpcSs"), "r", host);
        denied.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var created = await Create(op, "svc.start", id, Svc("Spooler"));
        var detail = (await created.Content.ReadFromJsonAsync<JobDetail>())!;
        // Report a failure before the fake executor finishes its simulated run (the first final state wins).
        using (var scope = factory.Services.CreateScope())
        {
            var reports = scope.ServiceProvider.GetRequiredService<IJobReports>();
            (await reports.CompletedAsync(detail.Targets[0].Id, TargetState.Failed, 1, "Stop failed (return code 3).", null)).Ok.Should().BeTrue();
        }

        var rows = factory.Query(db => db.AuditEntries.Where(a => a.ActorSid == sid).OrderBy(a => a.Id).ToList());
        rows.Should().Contain(a => a.Action == "job.denied" && !a.Success && a.ActorAccount == "TEST\\user");
        rows.Should().Contain(a => a.Action == "job.create" && a.Success && a.NewValue == "svc.start");
        rows.Should().Contain(a => a.Action == "job.target" && !a.Success && a.NewValue == "Failed" && a.TargetId == detail.Targets[0].Host);
    }

    // ---------- settings API: Admin list, read-only floor, validation, audit

    [Fact]
    public async Task An_operator_cannot_change_the_settings()
    {
        var (op, _) = await UserOnAsync(factory, Role.Operator);

        (await op.PutAsJsonAsync("/api/settings", new PlatformSettings { ProtectedServices = ["X"] })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await op.GetAsync("/api/settings")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_floor_cannot_be_removed_through_the_settings_api_and_a_client_supplied_floor_is_ignored()
    {
        var (admin, sid) = await UserOnAsync(factory, Role.Admin);
        var current = (await admin.GetFromJsonAsync<SettingsEnvelope>("/api/settings"))!.Settings;
        var floor = ProtectedNames.Services.ToArray();

        var body = new Dictionary<string, object?>
        {
            ["pollIntervalSeconds"] = current.PollIntervalSeconds, ["maxConcurrency"] = current.MaxConcurrency, ["cpuAlertPercent"] = current.CpuAlertPercent,
            ["ramAlertPercent"] = current.RamAlertPercent, ["diskAlertPercent"] = current.DiskAlertPercent, ["offlineAfterFailures"] = current.OfflineAfterFailures,
            ["metricRetentionDays"] = current.MetricRetentionDays, ["diskPollMinutes"] = current.DiskPollMinutes, ["jobParallelism"] = current.JobParallelism,
            ["protectedHosts"] = current.ProtectedHosts, ["protectedServices"] = Array.Empty<string>(), ["protectedServicesFloor"] = Array.Empty<string>(),
        };
        var put = await admin.PutAsJsonAsync("/api/settings", body);

        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        var json = await JsonOf(put);
        json.GetProperty("protectedServices").GetArrayLength().Should().Be(0);
        json.GetProperty("protectedServicesFloor").EnumerateArray().Select(x => x.GetString()).Should().BeEquivalentTo(floor);
        json.GetProperty("protectedServicesFloor").EnumerateArray().Select(x => x.GetString()).Should().Contain(["RpcSs", "DcomLaunch", "LSM", "WinDefend", "EventLog", "Netlogon"]);
        var get = await JsonOf(await admin.GetAsync("/api/settings"));
        get.GetProperty("settings").GetProperty("protectedServicesFloor").GetArrayLength().Should().Be(floor.Length);
        get.GetProperty("settings").TryGetProperty("protectedServices", out _).Should().BeTrue();
        var guard = factory.Services.CreateScope().ServiceProvider.GetRequiredService<IServiceGuard>();
        (await guard.IsProtectedAsync("RpcSs")).Should().BeTrue("the floor still holds after saving an empty list");
        sid.Should().NotBeNull();
    }

    [Fact]
    public async Task The_admin_list_is_normalised_validated_and_the_change_is_audited()
    {
        var (admin, sid) = await UserOnAsync(factory, Role.Admin);
        var current = (await admin.GetFromJsonAsync<SettingsEnvelope>("/api/settings"))!.Settings;

        current.ProtectedServices = [" CorpAgentA ", "corpagenta", "CorpAgentB", "rpcss"];
        var put = await admin.PutAsJsonAsync("/api/settings", current);
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        (await JsonOf(put)).GetProperty("protectedServices").EnumerateArray().Select(x => x.GetString()).Should().Equal("CorpAgentA", "CorpAgentB");
        var row = factory.Query(db => db.AuditEntries.Where(a => a.ActorSid == sid && a.Action == "settings.update").OrderBy(a => a.Id).Last());
        row.Success.Should().BeTrue();
        row.NewValue.Should().Contain("CorpAgentA").And.Contain("CorpAgentB");

        foreach (var bad in new[] { new[] { "bad;name" }, new[] { "a\"b" }, Enumerable.Range(0, ServiceNameRules.MaxProtected + 1).Select(i => $"S{i}").ToArray() })
        {
            current.ProtectedServices = [.. bad];
            (await admin.PutAsJsonAsync("/api/settings", current)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        current.ProtectedServices = [];   // leave the shared settings as they were
        (await admin.PutAsJsonAsync("/api/settings", current)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---------- no hub method creates jobs

    [Fact]
    public void No_SignalR_hub_method_can_create_confirm_or_run_a_job_and_every_method_has_an_explicit_policy()
    {
        var allowed = new Dictionary<Type, string[]>
        {
            [typeof(NexoraHub)] = ["SubscribeJob", "UnsubscribeJob", "SubscribeEndpoint", "UnsubscribeEndpoint"],
            [typeof(HelperHub)] = ["JobTargetStarted", "JobLog", "JobProgress", "JobTargetCompleted"],
        };
        foreach (var (hub, names) in allowed)
        {
            var methods = hub.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetBaseDefinition().DeclaringType == hub).ToList();   // not the overrides of Hub's own lifecycle methods
            methods.Select(m => m.Name).Should().BeEquivalentTo(names, $"{hub.Name} has exactly these client-callable methods");

            var classPolicy = hub.GetCustomAttribute<AuthorizeAttribute>()?.Policy;
            classPolicy.Should().NotBeNullOrEmpty($"{hub.Name} has an explicit policy");
            foreach (var m in methods)
            {
                var policy = m.GetCustomAttribute<AuthorizeAttribute>()?.Policy ?? classPolicy;
                policy.Should().NotBeNullOrEmpty($"{hub.Name}.{m.Name}");
                m.GetParameters().Select(p => p.ParameterType).Should().NotContain(t => t == typeof(CreateJobRequest) || t == typeof(ConfirmRequest) || t == typeof(PreflightRequest),
                    $"{hub.Name}.{m.Name} takes no job request");
            }
        }
        typeof(HelperHub).GetConstructors().Single().GetParameters().Select(p => p.ParameterType)
            .Should().NotContain([typeof(IJobService), typeof(IConfirmTokens), typeof(IServiceGuard)], "the helper hub only receives reports; it cannot create jobs");
        typeof(NexoraHub).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).Should().NotContain([typeof(IJobService), typeof(IConfirmTokens)]);
    }

    private sealed record SettingsEnvelope(PlatformSettings Settings);

    // ---------- start of a floor service is remediation

    [Fact]
    public async Task An_operator_can_start_a_stopped_floor_service_and_it_is_audited_while_stop_and_restart_stay_refused()
    {
        var (op, sid) = await UserOnAsync(factory, Role.Operator);
        var (id, host) = await OnlinePcAsync();
        FakeServiceCollector.ServicesByHost[host] = [new("Netlogon", "Netlogon", "Stopped", "Auto"), new("RpcSs", "RPC", "Running", "Auto")];

        var rows = (await JsonOf(await op.GetAsync($"/api/endpoints/{id}/services"))).EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!);
        rows["Netlogon"].GetProperty("protected").GetBoolean().Should().BeTrue();
        Allowed(rows["Netlogon"]).Should().Equal("start");
        Allowed(rows["RpcSs"]).Should().BeEmpty();

        (await Confirm(op, "svc.start", id, Svc("Netlogon"))).StatusCode.Should().Be(HttpStatusCode.OK);
        var created = await Create(op, "svc.start", id, Svc("netlogon "));
        created.StatusCode.Should().Be(HttpStatusCode.Accepted, await created.Content.ReadAsStringAsync());
        factory.Query(db => db.AuditEntries.Single(a => a.ActorSid == sid && a.Action == "job.create")).Details.Should().ContainEquivalentOf("\"serviceName\":\"netlogon\"");
        Denied(sid).Should().BeEmpty();

        (await Create(op, "svc.stop", id, Svc("Netlogon"), "r", "x")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Create(op, "svc.restart", id, Svc("Netlogon"), "r", "x")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Denied(sid).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_settings_save_that_omits_protectedServices_keeps_the_stored_list()
    {
        var (admin, _) = await UserOnAsync(factory, Role.Admin);
        var current = (await admin.GetFromJsonAsync<SettingsEnvelope>("/api/settings"))!.Settings;
        current.ProtectedServices = ["KeepMeA"];
        (await admin.PutAsJsonAsync("/api/settings", current)).StatusCode.Should().Be(HttpStatusCode.OK);
        try
        {
            var put = await admin.PutAsJsonAsync("/api/settings", new { pollIntervalSeconds = current.PollIntervalSeconds, protectedHosts = current.ProtectedHosts });
            put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
            (await JsonOf(put)).GetProperty("protectedServices").EnumerateArray().Select(x => x.GetString()).Should().Equal("KeepMeA");

            current.ProtectedServices = [];
            var cleared = await admin.PutAsJsonAsync("/api/settings", current);
            (await JsonOf(cleared)).GetProperty("protectedServices").GetArrayLength().Should().Be(0, "an explicit empty list still clears it");
        }
        finally
        {
            current.ProtectedServices = [];
            await admin.PutAsJsonAsync("/api/settings", current);
        }
    }

    [Theory]
    [InlineData(Role.Operator, "WinRM")]
    [InlineData(Role.Operator, "  remoteregistry ")]
    [InlineData(Role.Admin, "sshd")]
    [InlineData(Role.Admin, "TlntSvr")]
    public async Task Starting_a_remote_management_service_is_refused_at_every_stage_and_audited(Role role, string name)
    {
        var (client, sid) = await UserOnAsync(factory, role);
        var (id, _) = await OnlinePcAsync();

        (await Confirm(client, "svc.start", id, Svc(name))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Create(client, "svc.start", id, Svc(name))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/jobs/preflight", new { actionCode = "svc.start", endpointIds = new[] { id }, parameters = Svc(name) })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        factory.Query(db => db.Jobs.Any(j => j.InitiatorSid == sid)).Should().BeFalse();
        Denied(sid).Should().HaveCount(3).And.OnlyContain(r => !r.Success && r.Details!.Contains("protected"));
    }

    [Fact]
    public async Task Start_on_several_workstations_needs_a_confirmation_token()
    {
        var (op, sid) = await UserOnAsync(factory, Role.Operator);
        var (a, _) = await OnlinePcAsync();
        var (b, _) = await OnlinePcAsync();
        var both = new { actionCode = "svc.start", endpointIds = new[] { a, b }, parameters = Svc("Spooler") };

        (await op.PostAsJsonAsync("/api/jobs", both)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "Medium needs a token");
        var confirm = await op.PostAsJsonAsync("/api/jobs/confirm", both);
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await confirm.Content.ReadFromJsonAsync<ConfirmResponse>())!.Token;
        (await op.PostAsJsonAsync("/api/jobs", new { both.actionCode, both.endpointIds, both.parameters, confirmToken = token })).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await Create(op, "svc.start", a, Svc("Spooler"))).StatusCode.Should().Be(HttpStatusCode.Accepted, "a single target stays Low");
        sid.Should().NotBeNull();
    }
}
