using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Application;
using Nexora.Application.Jobs;
using Nexora.Application.Services;
using Nexora.Domain;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Api.Tests;

/// <summary>F2 carry-overs from the S0a/S0b reviews: escalator gap, not-implemented denials, and the unique-violation path of the inventory insert.</summary>
public sealed class F2CarryOverTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    private sealed class FixedRisk(RiskLevel level) : IRiskEscalator
    {
        public Task<RiskLevel> EffectiveRiskAsync(ActionDefinition def, IReadOnlyList<ManagedEndpoint> targets, IReadOnlyDictionary<string, string> parameters, CancellationToken ct) =>
            Task.FromResult(level);
    }

    private static HttpClient Client(WebApplicationFactory<Program> f, string sid)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add(TestAuthHandler.SidHeader, sid);
        c.DefaultRequestHeaders.Add(TestAuthHandler.AccountHeader, "TEST\\user");
        c.DefaultRequestHeaders.Add(TestAuthHandler.GroupsHeader, TestIdentities.AccessGroupSid);
        return c;
    }

    private async Task<Guid> SeedServerOsAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
        var e = new ManagedEndpoint { Id = Guid.NewGuid(), Hostname = "SRV-" + Guid.NewGuid().ToString("N")[..6], Online = true, OsCaption = "Microsoft Windows Server 2022", CreatedUtc = DateTime.UtcNow };
        db.Endpoints.Add(e);
        await db.SaveChangesAsync();
        return e.Id;
    }

    // ---------- (b) S0b gap: an escalator that returns LOWER than the catalog cannot switch the protected check off

    [Theory]
    [InlineData("svc.stop")]
    [InlineData("svc.restart")]
    public async Task A_lower_risk_from_the_escalator_still_blocks_a_server_os_and_still_shows_it_in_the_preflight(string code)
    {
        var sid = TestIdentities.NewSid();
        await factory.SeedUserAsync(sid, Role.Operator);
        using var lowered = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<IRiskEscalator>(new FixedRisk(RiskLevel.Low)))));
        var client = await NexoraFactory.WithTokenAsync(Client(lowered, sid));
        var server = await SeedServerOsAsync();
        var p = new { serviceName = "Spooler" };

        var confirm = await client.PostAsJsonAsync("/api/jobs/confirm", new { actionCode = code, endpointIds = new[] { server }, parameters = p });
        var create = await client.PostAsJsonAsync("/api/jobs", new { actionCode = code, endpointIds = new[] { server }, parameters = p });
        var pre = await client.PostAsJsonAsync("/api/jobs/preflight", new { actionCode = code, endpointIds = new[] { server }, parameters = p });

        confirm.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await confirm.Content.ReadAsStringAsync()).Should().Contain("Protected workstation(s)");
        create.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var result = (await pre.Content.ReadFromJsonAsync<PreflightResult>())!;
        result.Protected.Should().ContainSingle().Which.Reason.Should().Be("server OS");
        result.OverrideNeeded.Should().BeTrue();
        result.EffectiveRisk.Should().Be("Medium", "the catalog level is the floor");
        factory.Query(db => db.Jobs.Any(j => j.InitiatorSid == sid)).Should().BeFalse();
    }

    // ---------- (c) not-implemented refusals are audited and rate-limited like validation denials

    [Fact]
    public async Task A_not_implemented_action_is_refused_with_501_and_audited_as_a_denial()
    {
        var sid = TestIdentities.NewSid();
        var op = await factory.UserClientAsync(sid, Role.Operator);
        var ep = await factory.SeedEndpointAsync(online: true);
        ActionCatalog.Find("power.wake")!.Implemented.Should().BeFalse("this test needs a planned action");

        var confirm = await op.PostAsJsonAsync("/api/jobs/confirm", new { actionCode = "power.wake", endpointIds = new[] { ep } });
        var create = await op.PostAsJsonAsync("/api/jobs", new { actionCode = "power.wake", endpointIds = new[] { ep } });

        confirm.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
        create.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
        var rows = factory.Query(db => db.AuditEntries.Where(a => a.Action == "job.denied" && a.ActorSid == sid).OrderBy(a => a.Id).ToList());
        rows.Should().HaveCount(2).And.OnlyContain(r => !r.Success && r.NewValue == "power.wake");
        rows[0].Details.Should().Contain("\"kind\":\"NotImplemented\"").And.Contain("\"stage\":\"confirm\"");
        rows[1].Details.Should().Contain("\"kind\":\"NotImplemented\"").And.Contain("\"stage\":\"create\"");
        factory.Query(db => db.Jobs.Any(j => j.InitiatorSid == sid)).Should().BeFalse();
    }

    [Fact]
    public async Task Not_implemented_denials_share_the_validation_cap_and_do_not_flood_the_audit_log()
    {
        var sid = TestIdentities.NewSid();
        var op = await factory.UserClientAsync(sid, Role.Operator);
        var ep = await factory.SeedEndpointAsync(online: true);

        for (var i = 0; i < DenialRateLimiter.MaxValidationPerMinute + 6; i++)
            (await op.PostAsJsonAsync("/api/jobs/confirm", new { actionCode = "power.wake", endpointIds = new[] { ep } })).StatusCode.Should().Be(HttpStatusCode.NotImplemented);

        factory.Query(db => db.AuditEntries.Count(a => a.Action == "job.denied" && a.ActorSid == sid)).Should().Be(DenialRateLimiter.MaxValidationPerMinute);
        factory.Query(db => db.AuditEntries.Count(a => a.Action == "job.denied.suppressed" && a.ActorSid == sid)).Should().Be(1);
    }

    // ---------- (b)/(c) EndpointService.InsertAsync on a unique violation

    private sealed class FakePg(string state) : Exception("duplicate key value violates unique constraint") { public string SqlState { get; } = state; }

    /// <summary>The real context, except that the first <see cref="FailFor"/> saves fail with a PostgreSQL unique violation (23505).</summary>
    private sealed class FailingDb(NexoraDbContext inner, int failFor) : INexoraDb
    {
        public int Saves;
        public DbSet<User> Users => inner.Users;
        public DbSet<AccessRequest> AccessRequests => inner.AccessRequests;
        public DbSet<AuditEntry> AuditEntries => inner.AuditEntries;
        public DbSet<ManagedEndpoint> Endpoints => inner.Endpoints;
        public DbSet<ScriptDefinition> Scripts => inner.Scripts;
        public DbSet<ActionJob> ActionJobs => inner.ActionJobs;
        public DbSet<SettingsRow> Settings => inner.Settings;
        public DbSet<EndpointGroup> Groups => inner.Groups;
        public DbSet<EndpointGroupMember> GroupMembers => inner.GroupMembers;
        public DbSet<EndpointInventory> EndpointInventories => inner.EndpointInventories;
        public DbSet<MetricSample> MetricSamples => inner.MetricSamples;
        public DbSet<FleetSample> FleetSamples => inner.FleetSamples;
        public DbSet<DiskHealthSnapshot> DiskHealth => inner.DiskHealth;
        public DbSet<DiskTempSample> DiskTemps => inner.DiskTemps;
        public DbSet<Job> Jobs => inner.Jobs;
        public DbSet<JobTarget> JobTargets => inner.JobTargets;
        public DbSet<JobLogChunk> JobLogChunks => inner.JobLogChunks;
        public DbSet<ProfileSize> ProfileSizes => inner.ProfileSizes;
        public DbSet<DiagnosisRun> DiagnosisRuns => inner.DiagnosisRuns;
        public ChangeTracker ChangeTracker => inner.ChangeTracker;
        public DatabaseFacade Database => inner.Database;

        public Task<int> SaveChangesAsync(CancellationToken ct = default) =>
            ++Saves <= failFor ? throw new DbUpdateException("save failed", new FakePg("23505")) : inner.SaveChangesAsync(ct);
    }

    private sealed class Clock : ISystemClock { public DateTime UtcNow => new(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc); }

    private sealed class NoNotifier : INexoraNotifier
    {
        public Task AccessRequestPendingAsync(AccessRequestDto request) => Task.CompletedTask;
        public Task AccessChangedAsync(string sid) => Task.CompletedTask;
        public Task EndpointUpdatedAsync(EndpointSummaryDto endpoint) => Task.CompletedTask;
        public Task DashboardChangedAsync() => Task.CompletedTask;
    }

    private static (NexoraDbContext Ctx, FailingDb Db, EndpointService Service) Build(int failFor)
    {
        var ctx = new NexoraDbContext(new DbContextOptionsBuilder<NexoraDbContext>().UseInMemoryDatabase("insert-" + Guid.NewGuid()).Options);
        var db = new FailingDb(ctx, failFor);
        var clock = new Clock();
        var service = new EndpointService(db, new FakeCollector(), new FakeDirectory(), new EndpointDtoFactory(db, new SettingsService(db, new AuditService(db, clock), clock), clock),
            new AuditService(db, clock), new NoNotifier(), clock, NullLogger<EndpointService>.Instance, new DetailReadGate());
        return (ctx, db, service);
    }

    private static readonly Actor Admin = new("S-1-5-21-1-2-3-9001", "TEST\\admin", "10.0.0.5");

    [Fact]
    public async Task A_unique_violation_detaches_only_this_attempts_entities_and_audits_the_refusal_as_a_failure()
    {
        var (ctx, db, service) = Build(failFor: 1);
        using var _ = ctx;
        var unrelated = new ManagedEndpoint { Id = Guid.NewGuid(), Hostname = "unrelated-host", CreatedUtc = DateTime.UtcNow };
        var unrelatedGroup = new EndpointGroup { Id = Guid.NewGuid(), Name = "Unrelated", CreatedUtc = DateTime.UtcNow };
        ctx.Endpoints.Add(unrelated);
        ctx.Groups.Add(unrelatedGroup);

        var result = await service.AddAsync(Admin, [new EndpointInput(null, "pc-dup-1"), new EndpointInput("Second", "pc-dup-2")], [], CancellationToken.None);

        result.Kind.Should().Be(ErrorKind.Conflict);
        ctx.ChangeTracker.Entries<ManagedEndpoint>().Select(e => e.Entity.Hostname).Should().Equal(["unrelated-host"], "this attempt's endpoints were detached, the unrelated one was not");
        ctx.Entry(unrelated).State.Should().NotBe(EntityState.Detached);
        ctx.Entry(unrelatedGroup).State.Should().NotBe(EntityState.Detached);
        var audit = ctx.AuditEntries.AsNoTracking().Where(a => a.Action == "endpoint.add").ToList();
        audit.Should().ContainSingle("the staged success row was detached; only the failure row remains");
        audit[0].Success.Should().BeFalse();
        audit[0].ActorSid.Should().Be(Admin.Sid);
        audit[0].Details.Should().Contain("already exists").And.Contain("\"count\":2");
        audit[0].NewValue.Should().Contain("pc-dup-1");
        ctx.Endpoints.AsNoTracking().Any(e => e.Hostname == "pc-dup-1").Should().BeFalse("nothing of the failed attempt was stored");
        db.Saves.Should().Be(2, "the failed insert, then the failure audit");
    }

    [Fact]
    public async Task When_even_the_failure_audit_cannot_be_saved_nothing_unrelated_is_touched_and_the_caller_still_gets_a_conflict()
    {
        var (ctx, _, service) = Build(failFor: 2);
        using var _ = ctx;
        var unrelated = new ManagedEndpoint { Id = Guid.NewGuid(), Hostname = "unrelated-host", CreatedUtc = DateTime.UtcNow };
        ctx.Endpoints.Add(unrelated);

        var result = await service.AddAsync(Admin, [new EndpointInput(null, "pc-dup-3")], [], CancellationToken.None);

        result.Kind.Should().Be(ErrorKind.Conflict);
        ctx.Entry(unrelated).State.Should().Be(EntityState.Added, "left exactly as it was");
        ctx.ChangeTracker.Entries<AuditEntry>().Should().BeEmpty("neither the success row nor the unsaved failure row stays staged");
        ctx.ChangeTracker.Entries<ManagedEndpoint>().Should().ContainSingle().Which.Entity.Hostname.Should().Be("unrelated-host");
    }

    [Fact]
    public async Task A_refused_directory_import_is_audited_as_a_failure_too()
    {
        var (ctx, _, service) = Build(failFor: 1);
        using var _ = ctx;

        var result = await service.ImportFromDirectoryAsync(Admin, "PC-AD", CancellationToken.None);

        result.Kind.Should().Be(ErrorKind.Conflict);
        ctx.AuditEntries.AsNoTracking().Should().ContainSingle(a => a.Action == "endpoint.import" && !a.Success);
    }
}
