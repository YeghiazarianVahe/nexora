using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexora.Application;
using Nexora.Application.Jobs;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Api.Tests;

/// <summary>S0a: every refused job request leaves a job.denied audit row (success=false) for the operator; a failing audit write never breaks the response.</summary>
public sealed class JobDenialAuditTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    private static object P() => new { steps = 1, delayMs = 10, outcome = "success" };

    private List<AuditEntry> Denied(string sid) =>
        factory.Query(db => db.AuditEntries.Where(a => a.Action == "job.denied" && a.ActorSid == sid).OrderBy(a => a.Id).ToList());

    private async Task<Guid> SeedServerAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
        var e = new ManagedEndpoint { Id = Guid.NewGuid(), Hostname = "SRV-" + Guid.NewGuid().ToString("N")[..6], Online = true, OsCaption = "Microsoft Windows Server 2022", CreatedUtc = DateTime.UtcNow };
        db.Endpoints.Add(e);
        await db.SaveChangesAsync();
        return e.Id;
    }

    private static Task<HttpResponseMessage> Confirm(HttpClient c, string action, Guid[] ids, string? typed = null, string? reason = "r", bool over = false, object? p = null) =>
        c.PostAsJsonAsync("/api/jobs/confirm", new { actionCode = action, endpointIds = ids, parameters = p ?? P(), reason, typed, overrideProtected = over });

    [Fact]
    public async Task A_role_that_is_too_low_is_denied_and_audited()
    {
        var sid = TestIdentities.NewSid();
        var op = await factory.UserClientAsync(sid, Role.Operator, "TEST\\op1");
        var ep = await factory.SeedEndpointAsync(online: true);

        (await Confirm(op, "demo.simulate.critical", [ep])).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var row = Denied(sid).Should().ContainSingle().Subject;
        row.Success.Should().BeFalse();
        row.ActorAccount.Should().Be("TEST\\op1");
        row.NewValue.Should().Be("demo.simulate.critical");
        row.TargetId.Should().Be(ep.ToString());
        row.Details.Should().Contain("needs the Admin role").And.Contain("\"stage\":\"confirm\"").And.Contain("\"outcome\":\"success\"");
    }

    [Fact]
    public async Task A_non_admin_protected_override_is_denied_and_audited()
    {
        var sid = TestIdentities.NewSid();
        var op = await factory.UserClientAsync(sid, Role.Operator);
        var srv = await SeedServerAsync();

        (await Confirm(op, "demo.simulate.high", [srv], typed: "x", over: true)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        Denied(sid).Should().ContainSingle().Which.Details.Should().Contain("Only an Admin can override");
    }

    [Fact]
    public async Task A_missing_confirmation_token_is_denied_and_audited()
    {
        var sid = TestIdentities.NewSid();
        var admin = await factory.UserClientAsync(sid, Role.Admin);
        var ep = await factory.SeedEndpointAsync(online: true);

        var r = await admin.PostAsJsonAsync("/api/jobs", new { actionCode = "demo.simulate.medium", endpointIds = new[] { ep }, parameters = P(), reason = "r", confirmToken = "garbage" });

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var row = Denied(sid).Should().ContainSingle().Subject;
        row.Details.Should().Contain("Confirmation is missing").And.Contain("\"stage\":\"create\"");
        factory.Query(db => db.Jobs.Any(j => j.InitiatorSid == sid)).Should().BeFalse("a denied request creates no job");
    }

    [Fact]
    public async Task A_typed_confirmation_mismatch_is_denied_and_audited()
    {
        var sid = TestIdentities.NewSid();
        var admin = await factory.UserClientAsync(sid, Role.Admin);
        var ep = await factory.SeedEndpointAsync(online: true);

        (await Confirm(admin, "demo.simulate.high", [ep], typed: "not-the-name")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        Denied(sid).Should().ContainSingle().Which.Details.Should().Contain("Type the workstation name");
    }

    [Fact]
    public async Task Validation_failures_are_denied_and_audited_and_parameters_are_size_capped()
    {
        var sid = TestIdentities.NewSid();
        var admin = await factory.UserClientAsync(sid, Role.Admin);
        var ep = await factory.SeedEndpointAsync(online: true);
        var huge = new string('A', 50_000);

        (await Confirm(admin, "demo.simulate", [ep], p: new { steps = 1, delayMs = 10, outcome = huge, password = "hunter2" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Confirm(admin, "no.such.action", [ep])).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Confirm(admin, "demo.simulate", [])).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var rows = Denied(sid);
        rows.Should().HaveCount(3);
        rows[0].Details!.Length.Should().BeLessThan(4_000, "the denied parameters are capped");
        rows[0].Details.Should().NotContain("hunter2");
        rows.Should().OnlyContain(r => !r.Success);
    }

    [Fact]
    public async Task A_successful_confirm_writes_no_denial()
    {
        var sid = TestIdentities.NewSid();
        var admin = await factory.UserClientAsync(sid, Role.Admin);
        var ep = await factory.SeedEndpointAsync(online: true);

        (await Confirm(admin, "demo.simulate.medium", [ep])).StatusCode.Should().Be(HttpStatusCode.OK);

        Denied(sid).Should().BeEmpty();
    }

    [Fact]
    public async Task A_failing_audit_write_never_breaks_the_response()
    {
        var sid = TestIdentities.NewSid();
        await factory.SeedUserAsync(sid, Role.Admin);
        var ep = await factory.SeedEndpointAsync(online: true);
        using var broken = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.Replace(ServiceDescriptor.Scoped<IAuditService, ThrowingAudit>())));
        var client = await NexoraFactory.WithTokenAsync(Client(broken, sid));

        var r = await Confirm(client, "demo.simulate.high", [ep], typed: "wrong");

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the caller still gets the validation error, not a 500");
    }

    [Fact]
    public async Task The_confirmation_tier_follows_an_escalated_risk()
    {
        var sid = TestIdentities.NewSid();
        await factory.SeedUserAsync(sid, Role.Operator);
        var ep = await factory.SeedEndpointAsync(online: true);
        using var raised = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<IRiskEscalator>(new FixedRisk(RiskLevel.High)))));
        var client = await NexoraFactory.WithTokenAsync(Client(raised, sid));
        var host = factory.Query(db => db.Endpoints.Single(e => e.Id == ep).Hostname);

        // demo.simulate is Low in the catalog: without the hook it would need neither a token nor a typed name.
        (await Confirm(client, "demo.simulate", [ep], typed: "wrong")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/jobs", new { actionCode = "demo.simulate", endpointIds = new[] { ep }, parameters = P(), reason = "r" })).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "the escalated tier needs a confirmation token");
        Denied(sid).Should().HaveCount(2);
        (await Confirm(client, "demo.simulate", [ep], typed: host)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Without_an_escalator_a_low_risk_action_needs_no_confirmation()
    {
        var sid = TestIdentities.NewSid();
        var op = await factory.UserClientAsync(sid, Role.Operator);
        var ep = await factory.SeedEndpointAsync(online: true);

        (await op.PostAsJsonAsync("/api/jobs", new { actionCode = "demo.simulate", endpointIds = new[] { ep }, parameters = P() })).StatusCode.Should().Be(HttpStatusCode.Accepted);
        Denied(sid).Should().BeEmpty();
    }

    private async Task<(HttpClient Client, Guid Ep, string Sid)> WithEscalatorAsync(IRiskEscalator esc, Role role = Role.Operator)
    {
        var sid = TestIdentities.NewSid();
        await factory.SeedUserAsync(sid, role);
        var ep = await factory.SeedEndpointAsync(online: true);
        var f = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<IRiskEscalator>(esc))));
        return (await NexoraFactory.WithTokenAsync(Client(f, sid)), ep, sid);
    }

    [Fact]
    public async Task A_tier_change_between_confirm_and_create_invalidates_the_token()
    {
        var (client, ep, sid) = await WithEscalatorAsync(new MediumThenHigh());
        var token = (await (await Confirm(client, "demo.simulate", [ep])).Content.ReadFromJsonAsync<ConfirmResponse>())!.Token;

        var r = await client.PostAsJsonAsync("/api/jobs", new { actionCode = "demo.simulate", endpointIds = new[] { ep }, parameters = P(), reason = "r", confirmToken = token });

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the token was issued for the Medium tier");
        Denied(sid).Should().ContainSingle().Which.Details.Should().Contain("Confirmation is missing");
    }

    [Fact]
    public async Task A_reason_is_required_when_the_effective_risk_is_high()
    {
        var (client, ep, _) = await WithEscalatorAsync(new FixedRisk(RiskLevel.High));
        var host = factory.Query(db => db.Endpoints.Single(e => e.Id == ep).Hostname);

        (await Confirm(client, "demo.simulate", [ep], typed: host, reason: null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Confirm(client, "demo.simulate", [ep], typed: host, reason: "because")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_escalator_cannot_lower_the_tier_of_a_high_risk_action()
    {
        var (client, ep, _) = await WithEscalatorAsync(new FixedRisk(RiskLevel.Low));

        (await Confirm(client, "demo.simulate.high", [ep], typed: "wrong-name")).StatusCode.Should().Be(HttpStatusCode.BadRequest, "typed confirmation still applies");
        (await client.PostAsJsonAsync("/api/jobs", new { actionCode = "demo.simulate.high", endpointIds = new[] { ep }, parameters = P(), reason = "r" })).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "a token is still required");
    }

    [Fact]
    public async Task An_escalated_risk_switches_the_protected_check_on_for_confirm_and_preflight()
    {
        var (client, _, sid) = await WithEscalatorAsync(new FixedRisk(RiskLevel.High));
        var srv = await SeedServerAsync();

        var r = await Confirm(client, "demo.simulate", [srv], typed: "x", reason: "r");

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await r.Content.ReadAsStringAsync()).Should().Contain("Protected workstation(s)");
        Denied(sid).Should().ContainSingle().Which.Details.Should().Contain("Protected workstation(s)");
        var pre = await client.PostAsJsonAsync("/api/jobs/preflight", new { actionCode = "demo.simulate", endpointIds = new[] { srv } });
        (await pre.Content.ReadFromJsonAsync<PreflightResult>())!.Protected.Should().ContainSingle().Which.Reason.Should().Be("server OS");
    }

    [Fact]
    public async Task Without_an_escalated_risk_a_low_action_is_not_protected_checked()
    {
        var sid = TestIdentities.NewSid();
        var op = await factory.UserClientAsync(sid, Role.Operator);
        var srv = await SeedServerAsync();

        (await Confirm(op, "demo.simulate", [srv])).StatusCode.Should().Be(HttpStatusCode.OK);
        var pre = await op.PostAsJsonAsync("/api/jobs/preflight", new { actionCode = "demo.simulate", endpointIds = new[] { srv } });
        (await pre.Content.ReadFromJsonAsync<PreflightResult>())!.Protected.Should().BeEmpty();
    }

    [Fact]
    public async Task One_request_that_closes_and_opens_a_suppression_window_writes_both_notices()
    {
        var sid = TestIdentities.NewSid();
        await factory.SeedUserAsync(sid, Role.Operator);
        var ep = await factory.SeedEndpointAsync(online: true);
        var f = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<IDenialRateLimiter>(new BothNotices()))));
        var client = await NexoraFactory.WithTokenAsync(Client(f, sid));

        (await Confirm(client, "demo.simulate.critical", [ep])).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var rows = factory.Query(db => db.AuditEntries.Where(a => a.Action == "job.denied.suppressed" && a.ActorSid == sid).OrderBy(a => a.Id).ToList());
        rows.Should().HaveCount(2);
        rows[0].Details.Should().Contain("\"windowClosed\":true");
        rows[1].Details.Should().Contain("\"windowClosed\":false");
        Denied(sid).Should().ContainSingle("the request's own denial row is still written");
    }

    [Fact]
    public void The_rate_limiter_returns_the_closing_and_the_opening_notice_together()
    {
        var limiter = new DenialRateLimiter(Microsoft.Extensions.Logging.Abstractions.NullLogger<DenialRateLimiter>.Instance);
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < DenialRateLimiter.MaxValidationPerMinute; i++) limiter.TryAcquire("S", ErrorKind.Validation, t0);
        limiter.TryAcquire("S", ErrorKind.Validation, t0.AddSeconds(1)).Notices.Should().ContainSingle().Which.WindowClosed.Should().BeFalse();
        for (var i = 0; i < DenialRateLimiter.MaxValidationPerMinute; i++) limiter.TryAcquire("S", ErrorKind.Validation, t0.AddSeconds(60.5)).Write.Should().BeTrue();

        var permit = limiter.TryAcquire("S", ErrorKind.Validation, t0.AddSeconds(61));

        permit.Write.Should().BeFalse();
        permit.Notices.Select(n => n.WindowClosed).Should().Equal(true, false);
    }

    [Fact]
    public async Task A_validation_flood_cannot_hide_a_later_forbidden_denial()
    {
        var sid = TestIdentities.NewSid();
        var op = await factory.UserClientAsync(sid, Role.Operator);
        var ep = await factory.SeedEndpointAsync(online: true);
        for (var i = 0; i < 30; i++) (await Confirm(op, "demo.simulate", [])).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await Confirm(op, "demo.simulate.critical", [ep])).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var rows = Denied(sid);
        rows.Count(r => r.Details!.Contains("\"kind\":\"Forbidden\"")).Should().Be(1);
        rows.Count(r => r.Details!.Contains("\"kind\":\"Validation\"")).Should().Be(DenialRateLimiter.MaxValidationPerMinute);
        var suppressed = factory.Query(db => db.AuditEntries.Where(a => a.Action == "job.denied.suppressed" && a.ActorSid == sid).ToList());
        suppressed.Should().ContainSingle().Which.Details.Should().Contain("Validation");
    }

    private static HttpClient Client(WebApplicationFactory<Program> f, string sid)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add(TestAuthHandler.SidHeader, sid);
        c.DefaultRequestHeaders.Add(TestAuthHandler.AccountHeader, "TEST\\user");
        c.DefaultRequestHeaders.Add(TestAuthHandler.GroupsHeader, TestIdentities.AccessGroupSid);
        return c;
    }

    private sealed class BothNotices : IDenialRateLimiter
    {
        public long Dropped => 0;
        public DenialPermit TryAcquire(string sid, ErrorKind kind, DateTime nowUtc) =>
            new(true, [new SuppressionNotice(7, "Validation=7", WindowClosed: true), new SuppressionNotice(1, "Validation=1", WindowClosed: false)]);
    }

    private sealed class FixedRisk(RiskLevel level) : IRiskEscalator
    {
        public Task<RiskLevel> EffectiveRiskAsync(ActionDefinition def, IReadOnlyList<ManagedEndpoint> targets, IReadOnlyDictionary<string, string> parameters, CancellationToken ct) =>
            Task.FromResult(level);
    }

    /// <summary>Medium on the first call (confirm), High afterwards (create).</summary>
    private sealed class MediumThenHigh : IRiskEscalator
    {
        private int _calls;
        public Task<RiskLevel> EffectiveRiskAsync(ActionDefinition def, IReadOnlyList<ManagedEndpoint> targets, IReadOnlyDictionary<string, string> parameters, CancellationToken ct) =>
            Task.FromResult(Interlocked.Increment(ref _calls) == 1 ? RiskLevel.Medium : RiskLevel.High);
    }

    private sealed class ThrowingAudit : IAuditService
    {
        public void Add(Actor actor, string action, string? targetType = null, string? targetId = null, string? oldValue = null, string? newValue = null, object? details = null, bool success = true) =>
            throw new InvalidOperationException("audit store is down");
        public Task<PagedResult<AuditEntryDto>> QueryAsync(AuditQuery query, CancellationToken ct = default) => throw new InvalidOperationException("audit store is down");
    }
}
