using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Nexora.Application;
using Nexora.Application.Jobs;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Api.Tests;

/// <summary>
/// M5 / F3: profile deletion is checked on the server against what the PC reports right now, is confirmed by typing the WORKSTATION name, is Admin only,
/// and the size in the audit entry is the server's own measurement (a client-supplied size is refused).
/// </summary>
public sealed class ProfileGuardTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    private const string Sid = "S-1-5-21-1-2-3-4001";

    private static ProfileInfo Profile(string sid = Sid, string? account = @"TEST\old.user", string path = @"C:\Users\old.user", bool loaded = false, bool special = false) =>
        new(sid, account, path, DateTime.UtcNow.AddDays(-90), loaded, special, false, null);

    private async Task<(HttpClient Admin, Guid Pc, string Host)> SetupAsync(params ProfileInfo[] profiles)
    {
        FakeCollector.ProfilesOverride = profiles;
        var pc = await factory.SeedEndpointAsync("PC-PROF", online: true);
        return (await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin), pc, factory.Query(db => db.Endpoints.Single(e => e.Id == pc).Hostname));
    }

    private static void Reset()
    {
        FakeCollector.ProfilesOverride = null;
        FakeProfileCollector.Users = null;
        FakeProfileCollector.Fail = false;
    }

    private static Task<HttpResponseMessage> ConfirmAsync(HttpClient c, Guid pc, object p, string? typed) =>
        c.PostAsJsonAsync("/api/jobs/confirm", new { actionCode = "profile.delete", endpointIds = new[] { pc }, parameters = p, reason = "left the bank", typed });

    private static readonly object Params = new { sid = Sid, localPath = @"C:\Users\old.user" };

    [Fact]
    public async Task The_workstation_name_must_be_typed_not_the_account_name()
    {
        try
        {
            var (admin, pc, host) = await SetupAsync(Profile());

            (await ConfirmAsync(admin, pc, Params, "old.user")).StatusCode.Should().Be(HttpStatusCode.BadRequest, "the account name no longer confirms a profile deletion");
            (await ConfirmAsync(admin, pc, Params, @"TEST\old.user")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ConfirmAsync(admin, pc, Params, "wrong-host")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ConfirmAsync(admin, pc, Params, null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ConfirmAsync(admin, pc, Params, host)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await ConfirmAsync(admin, pc, Params, host.ToUpperInvariant())).StatusCode.Should().Be(HttpStatusCode.OK, "case does not matter");
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task A_wrong_typed_name_is_audited_as_a_denial()
    {
        try
        {
            var (admin, pc, _) = await SetupAsync(Profile());
            var before = factory.Query(db => db.AuditEntries.Count(a => a.Action == "job.denied"));

            (await ConfirmAsync(admin, pc, Params, "old.user")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

            factory.Query(db => db.AuditEntries.Count(a => a.Action == "job.denied")).Should().Be(before + 1);
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task Operators_and_viewers_cannot_even_confirm_a_profile_deletion()
    {
        try
        {
            var (_, pc, host) = await SetupAsync(Profile());
            var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
            var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);

            (await ConfirmAsync(op, pc, Params, host)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await ConfirmAsync(viewer, pc, Params, host)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task A_profile_the_PC_does_not_report_or_at_another_path_is_refused()
    {
        try
        {
            var (admin, pc, host) = await SetupAsync(Profile());
            (await ConfirmAsync(admin, pc, new { sid = "S-1-5-21-1-2-3-9999", localPath = @"C:\Users\ghost" }, host)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var wrongPath = await ConfirmAsync(admin, pc, new { sid = Sid, localPath = @"C:\Users\someone.else" }, host);
            wrongPath.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await wrongPath.Content.ReadAsStringAsync()).Should().Contain("old.user");
        }
        finally { Reset(); }
    }

    [Theory]
    [InlineData(@"C:\Users\Default", false, false)]
    [InlineData(@"C:\Users\Public", false, false)]
    [InlineData(@"C:\Users\All Users", false, false)]
    [InlineData(@"C:\Users\x", true, false)]   // loaded: the user is signed in
    [InlineData(@"C:\Users\x", false, true)]   // special
    public async Task Default_public_system_and_signed_in_profiles_are_refused(string path, bool loaded, bool special)
    {
        try
        {
            var (admin, pc, host) = await SetupAsync(Profile(path: path, loaded: loaded, special: special));
            var r = await ConfirmAsync(admin, pc, new { sid = Sid, localPath = path }, host);
            r.StatusCode.Should().Be(HttpStatusCode.BadRequest, path);
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task A_user_in_an_interactive_session_is_refused_even_when_the_profile_is_not_loaded()
    {
        try
        {
            var (admin, pc, host) = await SetupAsync(Profile());
            FakeProfileCollector.Users = new SignedInUsers([Sid], []);
            var bySid = await ConfirmAsync(admin, pc, Params, host);
            bySid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await bySid.Content.ReadAsStringAsync()).Should().Contain("signed in");

            FakeProfileCollector.Users = new SignedInUsers([], [@"test\OLD.user"]);   // the console user, by name
            (await ConfirmAsync(admin, pc, Params, host)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task When_who_is_signed_in_cannot_be_determined_the_deletion_is_refused()
    {
        try
        {
            var (admin, pc, host) = await SetupAsync(Profile());
            FakeProfileCollector.Fail = true;

            var r = await ConfirmAsync(admin, pc, Params, host);

            r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var body = await r.Content.ReadAsStringAsync();
            body.Should().Contain("signed in").And.NotContain("secret-detail").And.NotContain("10.99.99.99");
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task An_account_that_does_not_resolve_is_refused()
    {
        try
        {
            var (admin, pc, host) = await SetupAsync(Profile(account: null));
            var r = await ConfirmAsync(admin, pc, Params, host);
            r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await r.Content.ReadAsStringAsync()).Should().Contain("could not be verified");
        }
        finally { Reset(); }
    }

    [Theory]
    [InlineData(@"C:\Users\.. ")]
    [InlineData(@"C:\Users\Public ")]
    [InlineData(@"C:\Users\Default.")]
    [InlineData(@"C:\Users\ADMINI~1")]
    [InlineData(@"C:\Users\DEFAUL~1")]
    [InlineData(@"C:\Users\%USERPROFILE%")]
    [InlineData("C:\\Users\\\u00dcnal")]
    [InlineData("C:\\Users\\x\u0001")]
    public async Task Folder_names_windows_would_normalise_are_refused_by_the_server(string path)
    {
        try
        {
            var (admin, pc, host) = await SetupAsync(Profile(path: path));
            (await ConfirmAsync(admin, pc, new { sid = Sid, localPath = path }, host)).StatusCode.Should().Be(HttpStatusCode.BadRequest, path);
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task Two_profiles_registered_on_one_folder_are_refused_by_the_server()
    {
        try
        {
            var (admin, pc, host) = await SetupAsync(
                Profile(path: @"C:\Users\bob"),
                new ProfileInfo("S-1-5-18", null, @"C:\Users\BOB", null, false, false, false, null));
            var r = await ConfirmAsync(admin, pc, new { sid = Sid, localPath = @"C:\Users\bob" }, host);
            r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await r.Content.ReadAsStringAsync()).Should().Contain("same folder");
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task A_protected_workstation_cannot_be_overridden_for_profile_deletion_and_the_refusal_is_audited()
    {
        try
        {
            var (admin, pc, host) = await SetupAsync(Profile());
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
                db.Endpoints.Single(e => e.Id == pc).OsCaption = "Microsoft Windows Server 2022";
                await db.SaveChangesAsync();
            }
            var before = factory.Query(db => db.AuditEntries.Count(a => a.Action == "job.denied"));
            var r = await admin.PostAsJsonAsync("/api/jobs/confirm", new { actionCode = "profile.delete", endpointIds = new[] { pc }, parameters = Params, reason = "r", typed = host, overrideProtected = true });

            r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await r.Content.ReadAsStringAsync()).Should().Contain("even with an override");
            factory.Query(db => db.AuditEntries.Count(a => a.Action == "job.denied")).Should().Be(before + 1);
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task A_second_profile_deletion_on_a_workstation_with_one_queued_is_a_409()
    {
        try
        {
            var (admin, pc, host) = await SetupAsync(Profile(), Profile("S-1-5-21-1-2-3-4002", @"TEST\two", @"C:\Users\two"));
            var token = (await (await ConfirmAsync(admin, pc, Params, host)).Content.ReadFromJsonAsync<ConfirmResponse>())!.Token;
            (await admin.PostAsJsonAsync("/api/jobs", new { actionCode = "profile.delete", endpointIds = new[] { pc }, parameters = Params, reason = "left the bank", confirmToken = token })).StatusCode.Should().Be(HttpStatusCode.Accepted);

            var second = new { sid = "S-1-5-21-1-2-3-4002", localPath = @"C:\Users\two" };
            (await ConfirmAsync(admin, pc, second, host)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task A_client_supplied_size_is_refused_and_the_audit_entry_carries_the_servers_own_measurement()
    {
        try
        {
            var (admin, pc, host) = await SetupAsync(Profile());
            var withClientSize = new { sid = Sid, localPath = @"C:\Users\old.user", sizeBytes = "123456789" };
            var refused = await ConfirmAsync(admin, pc, withClientSize, host);
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await refused.Content.ReadAsStringAsync()).Should().Contain("sizeBytes");

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
                db.ProfileSizes.Add(new ProfileSize { Id = Guid.NewGuid(), EndpointId = pc, Sid = Sid, SizeBytes = 7_654_321, MeasuredUtc = DateTime.UtcNow.AddHours(-2), JobId = Guid.NewGuid(), MeasuredBySid = "S-1-5-21-1-2-3-1000" });
                await db.SaveChangesAsync();
            }
            var token = (await (await ConfirmAsync(admin, pc, Params, host)).Content.ReadFromJsonAsync<ConfirmResponse>())!.Token;
            var created = await admin.PostAsJsonAsync("/api/jobs", new { actionCode = "profile.delete", endpointIds = new[] { pc }, parameters = Params, reason = "left the bank", confirmToken = token });

            created.StatusCode.Should().Be(HttpStatusCode.Accepted, await created.Content.ReadAsStringAsync());
            var details = factory.Query(db => db.AuditEntries.Where(a => a.Action == "job.create" && a.NewValue == "profile.delete").OrderByDescending(a => a.Id).First().Details)!;
            details.Should().Contain("old.user").And.Contain("7654321").And.Contain("TEST").And.NotContain("123456789").And.Contain("helper-reported").And.Contain("measuredBySid");
            factory.Query(db => db.Jobs.Where(j => j.ActionCode == "profile.delete").OrderByDescending(j => j.CreatedUtc).First().ParametersJson)
                .Should().Contain("7654321", "the helper gets the server's last measurement to report the size freed");
        }
        finally { Reset(); }
    }
}
