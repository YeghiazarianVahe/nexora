using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Nexora.Application;
using Nexora.Application.Jobs;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Api.Tests;

/// <summary>
/// Service, process, profile and session actions now exist only as catalog jobs. They get the same server-side role gate, protected-workstation
/// check, confirmation token, reason and audit as every other job; the old /api/actions path is gone.
/// </summary>
public sealed class RemoteActionJobTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    private static readonly Dictionary<string, string> Spooler = new() { ["serviceName"] = "Spooler" };

    private async Task<(Guid Id, string Host)> SeedAsync(string? osCaption = null)
    {
        var id = await factory.SeedEndpointAsync("PC-ACT", online: true);
        if (osCaption is not null)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
            db.Endpoints.Single(e => e.Id == id).OsCaption = osCaption;
            await db.SaveChangesAsync();
        }
        return (id, factory.Query(db => db.Endpoints.Single(e => e.Id == id).Hostname));
    }

    private Guid PinnedServerId() => factory.Query(db => db.Endpoints.Single(e => e.IsPinned).Id);

    /// <summary>Confirm (when the action needs it) and create, exactly as the UI does.</summary>
    private static async Task<HttpResponseMessage> RunAsync(
        HttpClient c, string action, Guid[] endpoints, object? parameters = null, string? reason = null, string? typed = null, bool overrideProtected = false)
    {
        string? token = null;
        if (ActionCatalog.Find(action)?.NeedsConfirmation == true)
        {
            var confirmed = await c.PostAsJsonAsync("/api/jobs/confirm", new { actionCode = action, endpointIds = endpoints, parameters, reason, typed, overrideProtected });
            if (!confirmed.IsSuccessStatusCode) return confirmed;
            token = (await confirmed.Content.ReadFromJsonAsync<ConfirmResponse>())!.Token;
        }
        return await c.PostAsJsonAsync("/api/jobs", new { actionCode = action, endpointIds = endpoints, parameters, reason, confirmToken = token, overrideProtected });
    }

    [Fact]
    public async Task The_legacy_action_endpoints_no_longer_exist()
    {
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var (id, _) = await SeedAsync();

        (await op.PostAsJsonAsync("/api/actions", new { type = "RestartService", endpointId = id, parameters = Spooler })).StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
        (await op.GetAsync("/api/actions")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await op.PostAsJsonAsync($"/api/groups/{Guid.NewGuid()}/actions", new { type = "RestartService" })).StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
        (await op.GetAsync("/api/helper/public-key")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("svc.start")]
    [InlineData("svc.stop")]
    [InlineData("svc.restart")]
    public async Task Operators_run_service_actions_as_confirmed_audited_jobs_bound_to_them(string code)
    {
        var sid = TestIdentities.NewSid();
        var op = await factory.UserClientAsync(sid, Role.Operator, "TEST\\op");
        var (id, _) = await SeedAsync();

        var response = await RunAsync(op, code, [id], Spooler);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var detail = (await response.Content.ReadFromJsonAsync<JobDetail>())!;
        detail.Job.InitiatorSid.Should().Be(sid);
        detail.Job.ActionCode.Should().Be(code);
        detail.Targets.Should().ContainSingle().Which.EndpointId.Should().Be(id);
        factory.Query(db => db.AuditEntries.Any(a => a.ActorSid == sid && a.Action == "job.create" && a.NewValue == code)).Should().BeTrue();
    }

    [Fact]
    public async Task A_service_action_without_a_confirmation_token_is_refused()
    {
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var (id, _) = await SeedAsync();

        var before = factory.Query(db => db.JobTargets.Count(t => t.EndpointId == id));

        var response = await op.PostAsJsonAsync("/api/jobs", new { actionCode = "svc.restart", endpointIds = new[] { id }, parameters = Spooler });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        factory.Query(db => db.JobTargets.Count(t => t.EndpointId == id)).Should().Be(before);
    }

    [Fact]
    public async Task Viewers_cannot_run_service_process_or_session_actions()
    {
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        var (id, _) = await SeedAsync();

        foreach (var (code, p) in new (string, object)[]
                 { ("svc.restart", Spooler), ("proc.kill", new { pid = 4242, processName = "notepad.exe" }), ("session.rdp", new { }), ("session.assist", new { }) })
        {
            var r = await RunAsync(viewer, code, [id], p);
            r.StatusCode.Should().Be(HttpStatusCode.Forbidden, code);
        }
    }

    [Fact]
    public async Task Parameters_are_validated_and_critical_system_names_are_refused()
    {
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var (id, _) = await SeedAsync();

        async Task<HttpStatusCode> Status(string code, object p) => (await RunAsync(op, code, [id], p)).StatusCode;

        (await Status("svc.restart", new { serviceName = "x\"; calc" })).Should().Be(HttpStatusCode.BadRequest);
        (await Status("svc.stop", new { serviceName = "WinDefend" })).Should().Be(HttpStatusCode.BadRequest, "critical services are on the deny list");
        (await Status("svc.restart", new { })).Should().Be(HttpStatusCode.BadRequest);
        (await Status("svc.restart", new { serviceName = "Spooler", extra = 1 })).Should().Be(HttpStatusCode.BadRequest, "unknown parameters are rejected");
        (await Status("proc.kill", new { pid = 4, processName = "notepad.exe" })).Should().Be(HttpStatusCode.BadRequest);
        (await Status("proc.kill", new { pid = 4242, processName = "lsass.exe" })).Should().Be(HttpStatusCode.BadRequest);
        (await Status("proc.kill", new { pid = 4242, processName = "a;b" })).Should().Be(HttpStatusCode.BadRequest);
        (await RunAsync(op, "svc.restart", [Guid.NewGuid()], Spooler)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "targets must come from the inventory");
    }

    [Fact]
    public async Task Process_kill_and_sessions_run_on_one_workstation_only()
    {
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var (a, _) = await SeedAsync();
        var (b, _) = await SeedAsync();

        (await RunAsync(op, "proc.kill", [a, b], new { pid = 4242, processName = "notepad.exe" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RunAsync(op, "session.rdp", [a, b], new { })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RunAsync(op, "proc.kill", [a], new { pid = 4242, processName = "notepad.exe" })).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await RunAsync(op, "session.rdp", [a], new { })).StatusCode.Should().Be(HttpStatusCode.Accepted, "low risk: no confirmation step");
    }

    [Fact]
    public async Task A_service_action_can_target_many_workstations_like_the_old_group_run()
    {
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++) ids.Add((await SeedAsync()).Id);

        var response = await RunAsync(op, "svc.restart", ids.ToArray(), Spooler);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await response.Content.ReadFromJsonAsync<JobDetail>())!.Job.TargetCount.Should().Be(4);
    }

    // ---------- protected workstations

    [Theory]
    [InlineData("svc.stop")]
    [InlineData("svc.restart")]
    public async Task Stopping_or_restarting_services_on_the_Nexora_server_needs_an_admin_override(string code)
    {
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var server = PinnedServerId();
        // An offline PC cannot have its dependents checked, which now counts as High (reason needed first); the real server is polled and online.
        await SetOnlineAsync(server, true);
        try
        {
            var blocked = await RunAsync(op, code, [server], Spooler);
            blocked.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await blocked.Content.ReadAsStringAsync()).Should().Contain("Protected");

            (await RunAsync(op, code, [server], Spooler, reason: "need it", overrideProtected: true)).StatusCode
                .Should().Be(HttpStatusCode.Forbidden, "only an Admin can override");
            (await RunAsync(admin, code, [server], Spooler, overrideProtected: true)).StatusCode
                .Should().Be(HttpStatusCode.BadRequest, "the override needs a reason");

            var ok = await RunAsync(admin, code, [server], Spooler, reason: "Print spooler stuck on the server", overrideProtected: true);
            ok.StatusCode.Should().Be(HttpStatusCode.Accepted, await ok.Content.ReadAsStringAsync());
            (await ok.Content.ReadFromJsonAsync<JobDetail>())!.Job.ProtectedOverride.Should().BeTrue();
        }
        finally { await SetOnlineAsync(server, false); }
    }

    private async Task SetOnlineAsync(Guid id, bool online)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
        db.Endpoints.Single(e => e.Id == id).Online = online;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Killing_a_process_on_the_Nexora_server_or_a_server_OS_is_blocked_for_operators()
    {
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var (serverOs, _) = await SeedAsync("Microsoft Windows Server 2022 Standard");
        var kill = new { pid = 4242, processName = "notepad.exe" };

        (await RunAsync(op, "proc.kill", [PinnedServerId()], kill)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var r = await RunAsync(op, "proc.kill", [serverOs], kill);
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await r.Content.ReadAsStringAsync()).Should().Contain("server OS");
    }

    [Fact]
    public async Task The_protected_list_from_settings_applies_to_these_actions()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var (id, host) = await SeedAsync();

        var settings = (await admin.GetFromJsonAsync<SettingsEnvelope>("/api/settings"))!.Settings;
        (await RunAsync(op, "svc.stop", [id], Spooler)).StatusCode.Should().Be(HttpStatusCode.Accepted);

        settings.ProtectedHosts = [.. settings.ProtectedHosts, host];
        (await admin.PutAsJsonAsync("/api/settings", settings)).StatusCode.Should().Be(HttpStatusCode.OK);
        try
        {
            var blocked = await RunAsync(op, "svc.stop", [id], Spooler);
            blocked.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await blocked.Content.ReadAsStringAsync()).Should().Contain("protected list");
            (await RunAsync(op, "svc.start", [id], Spooler)).StatusCode.Should().Be(HttpStatusCode.Accepted, "starting a service is not guarded");
        }
        finally
        {
            settings.ProtectedHosts = settings.ProtectedHosts.Where(h => h != host).ToList();
            await admin.PutAsJsonAsync("/api/settings", settings);
        }
    }

    // ---------- profiles

    [Fact]
    public async Task Only_admins_can_delete_profiles_and_the_job_is_typed_confirmed_with_a_reason()
    {
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        var adminSid = TestIdentities.NewSid();
        var admin = await factory.UserClientAsync(adminSid, Role.Admin);
        var (id, host) = await SeedAsync();
        var p = new { sid = "S-1-5-21-1-2-3-4001", localPath = @"C:\Users\old.user" };

        (await RunAsync(op, "profile.delete", [id], p, reason: "r", typed: host)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await RunAsync(viewer, "profile.delete", [id], p, reason: "r", typed: host)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await RunAsync(admin, "profile.delete", [id], p, typed: host)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "a reason is required");
        (await RunAsync(admin, "profile.delete", [id], p, reason: "left the bank", typed: "old.user")).StatusCode.Should().Be(HttpStatusCode.BadRequest, "the account name no longer confirms; the workstation name does");

        var ok = await RunAsync(admin, "profile.delete", [id], p, reason: "left the bank", typed: host);
        ok.StatusCode.Should().Be(HttpStatusCode.Accepted, await ok.Content.ReadAsStringAsync());
        factory.Query(db => db.AuditEntries.Any(a => a.ActorSid == adminSid && a.Action == "job.create" && a.NewValue == "profile.delete")).Should().BeTrue();
    }

    [Theory]
    [InlineData("S-1-5-18", @"C:\Users\x")]                              // SYSTEM
    [InlineData("S-1-5-21-1-2-3-4001", @"C:\Windows\System32")]           // not a profile folder
    [InlineData("S-1-5-21-1-2-3-4001", @"C:\Users\..\Windows")]
    [InlineData("S-1-5-21-1-2-3-4001", @"C:\Users\a\b")]
    [InlineData("not-a-sid", @"C:\Users\x")]
    public async Task System_and_non_profile_targets_are_refused(string sid, string path)
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var (id, host) = await SeedAsync();
        (await RunAsync(admin, "profile.delete", [id], new { sid, localPath = path }, reason: "r", typed: host)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Deleting_a_profile_on_the_Nexora_server_is_protected()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var server = factory.Query(db => db.Endpoints.Single(e => e.IsPinned));
        var p = new { sid = "S-1-5-21-1-2-3-4001", localPath = @"C:\Users\old.user" };

        var r = await RunAsync(admin, "profile.delete", [server.Id], p, reason: "r", typed: server.Hostname);
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await r.Content.ReadAsStringAsync()).Should().Contain("Protected");
    }

    // ---------- credential operations

    [Fact]
    public async Task No_catalog_action_handles_credentials_and_the_flag_stays_off()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var catalog = (await admin.GetFromJsonAsync<List<CatalogEntry>>("/api/jobs/catalog"))!;

        catalog.Should().NotContain(e => e.Code.Contains("password", StringComparison.OrdinalIgnoreCase)
                                          || e.Code.Contains("credential", StringComparison.OrdinalIgnoreCase)
                                          || e.DisplayName.Contains("password", StringComparison.OrdinalIgnoreCase));
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IFeatureGate>().CredentialOpsEnabled.Should().BeFalse();
        (await admin.PostAsJsonAsync("/api/jobs", new { actionCode = "account.resetpassword", endpointIds = new[] { Guid.NewGuid() } })).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
    }

    private sealed record SettingsEnvelope(PlatformSettings Settings);
}
