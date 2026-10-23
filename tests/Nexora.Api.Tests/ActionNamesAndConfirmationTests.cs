using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Nexora.Application.Jobs;
using Nexora.Domain;

namespace Nexora.Api.Tests;

/// <summary>Friendly names and commands for every catalog action, and confirmation by risk enforced by the server.</summary>
public sealed class ActionNamesAndConfirmationTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    // code, friendly name, command sub-label (null when there is none)
    private static readonly (string Code, string Name, string? Command)[] Expected =
    [
        ("power.restart", "Restart computer", "shutdown /r"), ("power.shutdown", "Shut down computer", "shutdown /s"),
        ("power.cancel", "Cancel pending restart/shutdown", "shutdown /a"), ("power.wake", "Wake computer", "Wake-on-LAN"),
        ("session.logoff", "Sign out user", null), ("session.lock", "Lock screen", null),
        ("session.message", "Send message to user", "msg *"), ("session.rdp", "Open Remote Desktop", "mstsc"),
        ("session.assist", "Offer Remote Assistance", "msra"),
        ("repair.sfc", "Repair system files", "sfc /scannow"), ("repair.dism.check", "Check component store", "DISM /CheckHealth"),
        ("repair.dism.scan", "Scan component store", "DISM /ScanHealth"), ("repair.dism.restore", "Repair Windows image", "DISM /RestoreHealth"),
        ("repair.dism.cleanup", "Clean up component store", "DISM /StartComponentCleanup"), ("disk.chkdsk.scan", "Check disk (read-only)", "chkdsk /scan"),
        ("disk.chkdsk.fix", "Fix disk at next boot", "chkdsk /f /r"), ("disk.smart", "Check disk health (SMART)", null),
        ("disk.cleanup", "Free up disk space", null), ("system.pendingreboot", "Pending reboot check", null), ("system.timesync", "Resync clock", "w32tm /resync"),
        ("update.scan", "Check for updates", null), ("update.install", "Install updates", null), ("update.history", "Update history", null),
        ("update.reset", "Reset Windows Update", null), ("defender.status", "Antivirus status", null),
        ("defender.signature", "Update antivirus definitions", null), ("defender.quickscan", "Quick antivirus scan", null),
        ("security.bitlocker", "Encryption status", null), ("security.localadmins", "Local administrators", null),
        ("net.gpupdate", "Apply group policy", "gpupdate /force"), ("net.gpresult", "Group policy report", "gpresult /r"),
        ("net.flushdns", "Flush DNS cache", "ipconfig /flushdns"), ("net.klist", "Purge Kerberos tickets", "klist purge"),
        ("net.diag", "Network diagnostics", null),
        ("svc.list", "List services", null), ("svc.start", "Start service", null), ("svc.stop", "Stop service", null), ("svc.restart", "Restart service", null),
        ("svc.spooler.reset", "Restart print spooler", null), ("proc.list", "List processes", null), ("proc.kill", "Stop process", null),
        ("sw.inventory", "Installed software", null), ("sw.startup", "Startup items", null), ("diag.eventlog", "Recent errors", null),
        ("diag.bundle", "Collect diagnostics bundle", null), ("diag.collect", "Collect diagnostics", null), ("profile.sizes", "Measure user profile sizes", null), ("profile.delete", "Delete user profile", null),
        ("script.run", "Run catalog script", null),
    ];

    private static IEnumerable<ActionDefinition> Real => ActionCatalog.All.Where(a => !a.Demo);

    [Fact]
    public void Every_real_action_has_its_friendly_name_command_and_a_one_line_description()
    {
        Real.Select(a => a.Code).Should().BeEquivalentTo(Expected.Select(e => e.Code), "a new action must get a friendly name here too");
        foreach (var (code, name, command) in Expected)
        {
            var def = ActionCatalog.Find(code)!;
            def.DisplayName.Should().Be(name, code);
            def.Command.Should().Be(command, code);
            def.Description.Should().NotBeNullOrWhiteSpace(code);
            def.Description.Should().NotContain("\n", code);
            def.Description.Length.Should().BeLessThan(90, $"{code} should be one short line");
            def.DisplayName.Should().NotContain(code, "the code stays internal");
        }
    }

    [Fact]
    public async Task The_catalog_api_returns_the_same_names_and_commands_the_panel_shows()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var catalog = (await admin.GetFromJsonAsync<List<CatalogEntry>>("/api/jobs/catalog"))!;

        foreach (var (code, name, command) in Expected)
        {
            var e = catalog.Single(c => c.Code == code);
            (e.DisplayName, e.Command).Should().Be((name, command), code);
        }
    }

    [Fact]
    public async Task Job_summaries_and_the_audit_log_carry_the_friendly_name_and_the_code()
    {
        var sid = TestIdentities.NewSid();
        var admin = await factory.UserClientAsync(sid, Role.Operator);
        var id = await factory.SeedEndpointAsync("PC-NAME", online: true);
        var body = new { actionCode = "profile.sizes", endpointIds = new[] { id }, parameters = new { } };

        var created = await admin.PostAsJsonAsync("/api/jobs", body);
        created.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var detail = (await created.Content.ReadFromJsonAsync<JobDetail>())!;

        detail.Job.ActionName.Should().Be("Measure user profile sizes");
        detail.Job.ActionCode.Should().Be("profile.sizes");
        factory.Query(db => db.AuditEntries.Any(a => a.ActorSid == sid && a.Action == "job.create" && a.NewValue == "profile.sizes")).Should().BeTrue();
    }

    [Fact]
    public async Task Simulated_and_planned_entries_are_hidden_unless_DemoActions_is_on()
    {
        using var off = factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:DemoActions"] = "false" })));
        var client = off.CreateClient();
        var sid = TestIdentities.NewSid();
        await factory.SeedUserAsync(sid, Role.Admin);
        client.DefaultRequestHeaders.Add(TestAuthHandler.SidHeader, sid);
        client.DefaultRequestHeaders.Add(TestAuthHandler.AccountHeader, "TEST\\user");
        client.DefaultRequestHeaders.Add(TestAuthHandler.GroupsHeader, TestIdentities.AccessGroupSid);

        var catalog = (await client.GetFromJsonAsync<List<CatalogEntry>>("/api/jobs/catalog"))!;

        catalog.Should().NotContain(e => e.Code.StartsWith("demo."));
        catalog.Should().OnlyContain(e => e.Implemented);
        catalog.Should().Contain(e => e.Code == "svc.restart" && e.DisplayName == "Restart service");
    }

    // ---------- risk levels

    [Fact]
    public void Risk_levels_drive_the_requirements_and_every_critical_action_is_admin_only()
    {
        foreach (var a in ActionCatalog.All)
        {
            a.NeedsConfirmation.Should().Be(a.Risk >= RiskLevel.Medium, a.Code);
            a.NeedsReason.Should().Be(a.Risk >= RiskLevel.High, a.Code);
            a.NeedsTypedConfirmation.Should().Be(a.Risk >= RiskLevel.High, a.Code);
            if (a.Risk == RiskLevel.Critical) a.MinRole.Should().Be(Role.Admin, $"{a.Code} is Critical");
            if (a.Risk >= RiskLevel.High) a.ChecksProtected.Should().BeTrue($"{a.Code} must respect protected workstations");
        }
    }

    [Fact]
    public void The_named_risk_examples_are_where_the_spec_puts_them()
    {
        ActionCatalog.Find("repair.dism.restore")!.Risk.Should().Be(RiskLevel.High);
        ActionCatalog.Find("power.restart")!.Risk.Should().Be(RiskLevel.High);
        ActionCatalog.Find("session.logoff")!.Risk.Should().Be(RiskLevel.High);
        ActionCatalog.Find("update.reset")!.Risk.Should().Be(RiskLevel.High);
        ActionCatalog.Find("disk.chkdsk.fix")!.Risk.Should().Be(RiskLevel.Critical);
        ActionCatalog.Find("update.install")!.Risk.Should().Be(RiskLevel.Critical);
        ActionCatalog.Find("profile.delete")!.Risk.Should().Be(RiskLevel.Critical);
        ActionCatalog.Find("repair.sfc")!.Risk.Should().Be(RiskLevel.Medium);
    }

    private static object? ParametersFor(string code) => code switch
    {
        "svc.start" or "svc.stop" or "svc.restart" => new { serviceName = "Spooler" },
        "proc.kill" => new { pid = 4242, processName = "notepad.exe" },
        "profile.delete" => new { sid = "S-1-5-21-1-2-3-4001", localPath = @"C:\Users\old.user" },
        "session.message" => new { message = "hello" },
        "script.run" => new { scriptId = Guid.NewGuid().ToString() },
        _ => null,
    };

    [Fact]
    public async Task The_server_refuses_a_missing_token_or_reason_for_every_Medium_High_and_Critical_action()
    {
        var id = await factory.SeedEndpointAsync("PC-RISK", true);
        var host = factory.Query(db => db.Endpoints.Single(e => e.Id == id).Hostname);

        foreach (var a in Real.Where(a => a.Risk >= RiskLevel.Medium))
        {
            // Each catalog case tests validation independently, rather than exhausting
            // one identity's deliberately enforced HTTP request budget across the catalog.
            using var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
            var p = ParametersFor(a.Code);
            async Task<HttpStatusCode> Create(string? reason, string? token = null) =>
                (await admin.PostAsJsonAsync("/api/jobs", new { actionCode = a.Code, endpointIds = new[] { id }, parameters = p, reason, confirmToken = token })).StatusCode;
            async Task<HttpStatusCode> Confirm(string? reason, string? typed) =>
                (await admin.PostAsJsonAsync("/api/jobs/confirm", new { actionCode = a.Code, endpointIds = new[] { id }, parameters = p, reason, typed })).StatusCode;

            (await Create("because", token: null)).Should().Be(HttpStatusCode.BadRequest, $"{a.Code}: no confirm token");
            (await Create("because", token: "garbage.token")).Should().Be(HttpStatusCode.BadRequest, $"{a.Code}: invalid token");

            if (a.Risk >= RiskLevel.High)
            {
                (await Create(null)).Should().Be(HttpStatusCode.BadRequest, $"{a.Code}: no reason");
                (await Confirm(null, host)).Should().Be(HttpStatusCode.BadRequest, $"{a.Code}: confirm without a reason");
                (await Confirm("because", null)).Should().Be(HttpStatusCode.BadRequest, $"{a.Code}: confirm without the typed hostname");
                (await Confirm("because", "someone-else")).Should().Be(HttpStatusCode.BadRequest, $"{a.Code}: wrong typed hostname");
            }
        }
    }

    [Fact]
    public async Task A_token_for_one_action_or_target_does_not_work_for_another()
    {
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var a = await factory.SeedEndpointAsync("PC-TOK-A", true);
        var b = await factory.SeedEndpointAsync("PC-TOK-B", true);
        var spooler = new { serviceName = "Spooler" };

        var token = (await (await op.PostAsJsonAsync("/api/jobs/confirm", new { actionCode = "svc.restart", endpointIds = new[] { a }, parameters = spooler }))
            .Content.ReadFromJsonAsync<ConfirmResponse>())!.Token;

        (await op.PostAsJsonAsync("/api/jobs", new { actionCode = "svc.stop", endpointIds = new[] { a }, parameters = spooler, confirmToken = token })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await op.PostAsJsonAsync("/api/jobs", new { actionCode = "svc.restart", endpointIds = new[] { b }, parameters = spooler, confirmToken = token })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await op.PostAsJsonAsync("/api/jobs", new { actionCode = "svc.restart", endpointIds = new[] { a }, parameters = new { serviceName = "Other" }, confirmToken = token })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await op.PostAsJsonAsync("/api/jobs", new { actionCode = "svc.restart", endpointIds = new[] { a }, parameters = spooler, confirmToken = token })).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await op.PostAsJsonAsync("/api/jobs", new { actionCode = "svc.restart", endpointIds = new[] { a }, parameters = spooler, confirmToken = token })).StatusCode.Should().Be(HttpStatusCode.BadRequest, "single use");
    }

    [Fact]
    public async Task Critical_actions_are_admin_only_even_with_a_valid_reason_and_typed_name()
    {
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var id = await factory.SeedEndpointAsync("PC-CRIT", true);
        var host = factory.Query(db => db.Endpoints.Single(e => e.Id == id).Hostname);

        foreach (var a in Real.Where(a => a.Risk == RiskLevel.Critical))
        {
            var r = await op.PostAsJsonAsync("/api/jobs/confirm", new { actionCode = a.Code, endpointIds = new[] { id }, parameters = ParametersFor(a.Code), reason = "because", typed = host });
            r.StatusCode.Should().Be(HttpStatusCode.Forbidden, a.Code);
        }
    }

    // ---------- pre-flight

    [Fact]
    public async Task Preflight_reports_offline_protected_and_busy_targets_and_needs_the_role()
    {
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        var online = await factory.SeedEndpointAsync("PC-PF-ON", true);
        var offline = await factory.SeedEndpointAsync("PC-PF-OFF", false);
        var server = factory.Query(db => db.Endpoints.Single(e => e.IsPinned).Id);

        var r = await op.PostAsJsonAsync("/api/jobs/preflight", new { actionCode = "svc.stop", endpointIds = new[] { online, offline, server } });
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await r.Content.ReadFromJsonAsync<PreflightResult>())!;

        result.Total.Should().Be(3);
        result.Offline.Should().HaveCount(2).And.Contain(n => n.StartsWith("PC-PF-OFF"));
        result.Reachable.Should().Be(1);
        result.Protected.Should().ContainSingle().Which.Reason.Should().Be("Nexora server");
        result.OverrideNeeded.Should().BeTrue();

        var start = (await (await op.PostAsJsonAsync("/api/jobs/preflight", new { actionCode = "svc.start", endpointIds = new[] { server } })).Content.ReadFromJsonAsync<PreflightResult>())!;
        start.OverrideNeeded.Should().BeFalse("starting a service is not guarded");
        (await viewer.PostAsJsonAsync("/api/jobs/preflight", new { actionCode = "svc.stop", endpointIds = new[] { online } })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await op.PostAsJsonAsync("/api/jobs/preflight", new { actionCode = "svc.stop", endpointIds = new[] { Guid.NewGuid() } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await op.PostAsJsonAsync("/api/jobs/preflight", new { actionCode = "nope", endpointIds = new[] { online } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Preflight_marks_targets_that_already_run_a_heavy_job()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var id = await factory.SeedEndpointAsync("PC-PF-BUSY", true);
        var host = factory.Query(db => db.Endpoints.Single(e => e.Id == id).Hostname);
        var p = new { steps = 1, delayMs = 0, outcome = "hang" };

        var confirm = await admin.PostAsJsonAsync("/api/jobs/confirm", new { actionCode = "demo.simulate.critical", endpointIds = new[] { id }, parameters = p, reason = "test", typed = host });
        var token = (await confirm.Content.ReadFromJsonAsync<ConfirmResponse>())!.Token;
        (await admin.PostAsJsonAsync("/api/jobs", new { actionCode = "demo.simulate.critical", endpointIds = new[] { id }, parameters = p, reason = "test", confirmToken = token })).StatusCode.Should().Be(HttpStatusCode.Accepted);

        var result = (await (await admin.PostAsJsonAsync("/api/jobs/preflight", new { actionCode = "demo.simulate.critical", endpointIds = new[] { id } })).Content.ReadFromJsonAsync<PreflightResult>())!;
        result.Busy.Should().ContainSingle();
    }
}
