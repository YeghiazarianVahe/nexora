using FluentAssertions;
using Nexora.Application;
using Nexora.Application.Jobs;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Api.Tests;

/// <summary>H2: a queued target is re-checked when it is about to start, and is skipped (not run late) when it went stale.</summary>
public sealed class QueueTtlTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    private static readonly JobsOptions Opts = new();

    private Task<(Job Job, JobTarget Target, ActionDefinition Def)> SeedAsync(string sid, Func<ActionDefinition, bool> pick, Guid endpoint, bool protectedOverride = false)
    {
        var def = ActionCatalog.All.First(pick);
        var job = new Job { Id = Guid.NewGuid(), ActionCode = def.Code, InitiatorSid = sid, InitiatorAccount = "TEST-user", CreatedUtc = DateTime.UtcNow, ProtectedOverride = protectedOverride };
        var host = factory.Query(db => db.Endpoints.Single(e => e.Id == endpoint).Hostname);
        var target = new JobTarget { Id = Guid.NewGuid(), JobId = job.Id, EndpointId = endpoint, EndpointName = "PC", Host = host };
        return Task.FromResult((job, target, def));
    }

    private async Task<string?> ReasonAsync(Job job, JobTarget target, ActionDefinition def, DateTime? now = null, DateTime? lastProgress = null)
    {
        using var scope = factory.Services.CreateScope();
        var settings = await scope.ServiceProvider.GetRequiredService<ISettingsService>().GetAsync();
        var n = now ?? DateTime.UtcNow;
        return await scope.ServiceProvider.GetRequiredService<JobOrchestrator>().SkipReasonAsync(job, def, target, settings, Opts, n, lastProgress ?? job.CreatedUtc, default);
    }

    private static bool Plain(ActionDefinition a) => a.Implemented && !a.IsPower && a.Risk <= RiskLevel.Medium && !a.ChecksProtected && a.MinRole <= Role.Operator;

    [Fact]
    public async Task A_readdressed_workstation_is_not_dispatched_to_the_old_host()
    {
        var sid = TestIdentities.NewSid(); await factory.SeedUserAsync(sid, Role.Operator);
        var ep = await factory.SeedEndpointAsync("PC-QA-ADDRESS");
        var (job, target, def) = await SeedAsync(sid, Plain, ep);
        target.Host = target.Host.ToLowerInvariant();
        (await ReasonAsync(job, target, def)).Should().BeNull("case alone does not change the host");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
            db.Endpoints.Single(e => e.Id == ep).Hostname = "NEW-" + Guid.NewGuid().ToString("N")[..8];
            await db.SaveChangesAsync();
        }
        (await ReasonAsync(job, target, def)).Should().Contain("address changed");
    }

    [Fact]
    public async Task A_fresh_job_for_a_current_operator_may_start()
    {
        var sid = TestIdentities.NewSid(); await factory.SeedUserAsync(sid, Role.Operator);
        var ep = await factory.SeedEndpointAsync("PC-TTL-OK");
        var (job, t, def) = await SeedAsync(sid, Plain, ep);
        (await ReasonAsync(job, t, def)).Should().BeNull();
    }

    [Fact]
    public async Task A_job_that_waited_past_the_ttl_is_skipped_and_power_actions_go_stale_faster()
    {
        var sid = TestIdentities.NewSid(); await factory.SeedUserAsync(sid, Role.Admin);
        var ep = await factory.SeedEndpointAsync("PC-TTL-OLD");

        var (job, t, def) = await SeedAsync(sid, Plain, ep);
        (await ReasonAsync(job, t, def, now: job.CreatedUtc.AddMinutes(9))).Should().BeNull("9 minutes is inside the 10-minute window");
        (await ReasonAsync(job, t, def, now: job.CreatedUtc.AddMinutes(11))).Should().Contain("Not started within 10 min");
        (await ReasonAsync(job, t, def, now: job.CreatedUtc.AddMinutes(11), lastProgress: job.CreatedUtc.AddMinutes(10))).Should().BeNull("progress restarts the clock, so a long batch is not cut off");

        var (pj, pt, pdef) = await SeedAsync(sid, a => a.Implemented && a.IsPower, ep, protectedOverride: true);
        (await ReasonAsync(pj, pt, pdef, now: pj.CreatedUtc.AddMinutes(3))).Should().Contain("Not started within 2 min");
    }

    [Fact]
    public async Task An_operator_demoted_while_the_job_was_queued_has_it_skipped()
    {
        var sid = TestIdentities.NewSid(); await factory.SeedUserAsync(sid, Role.Operator);
        var ep = await factory.SeedEndpointAsync("PC-TTL-DEMOTE");
        var (job, t, def) = await SeedAsync(sid, Plain, ep);
        (await ReasonAsync(job, t, def)).Should().BeNull();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
            db.Users.Single(u => u.Sid == sid).Role = Role.Viewer;
            await db.SaveChangesAsync();
            scope.ServiceProvider.GetRequiredService<IUserAccessCache>().Invalidate(sid);
        }

        (await ReasonAsync(job, t, def)).Should().Contain("no longer has the");
    }

    [Fact]
    public async Task The_configured_SuperAdmin_is_not_skipped_even_though_there_is_no_row_for_them_in_the_users_table()
    {
        // Found by the end-to-end run: the SuperAdmin is recognised by SID from configuration, so asking the users table found no role.
        factory.Query(db => db.Users.Any(u => u.Sid == TestIdentities.SuperAdminSid)).Should().BeFalse("the SuperAdmin has no users row in this suite");
        var ep = await factory.SeedEndpointAsync("PC-TTL-SUPER");
        var (job, t, def) = await SeedAsync(TestIdentities.SuperAdminSid, Plain, ep);

        (await ReasonAsync(job, t, def)).Should().BeNull();
    }

    [Fact]
    public async Task A_workstation_that_became_protected_or_vanished_is_skipped()
    {
        var sid = TestIdentities.NewSid(); await factory.SeedUserAsync(sid, Role.Admin);
        var ep = await factory.SeedEndpointAsync("PC-TTL-PROT");
        var (job, t, def) = await SeedAsync(sid, a => a.Implemented && a.ChecksProtected && !a.IsPower && a.Risk < RiskLevel.Critical, ep);
        (await ReasonAsync(job, t, def)).Should().BeNull();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
            db.Endpoints.Single(e => e.Id == ep).IsPinned = true;
            await db.SaveChangesAsync();
        }
        (await ReasonAsync(job, t, def)).Should().Contain("now protected");
        using (var scope = factory.Services.CreateScope())
        {
            // leave the shared database as it was: other tests count pinned servers
            var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
            db.Endpoints.Single(e => e.Id == ep).IsPinned = false;
            await db.SaveChangesAsync();
        }

        t.EndpointId = Guid.NewGuid();
        (await ReasonAsync(job, t, def)).Should().Contain("removed from the inventory");
    }
}
