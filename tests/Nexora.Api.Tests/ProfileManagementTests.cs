using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nexora.Application;
using Nexora.Application.Jobs;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Api.Tests;

/// <summary>
/// F3 user profile management: the server-filtered list, on-demand sizes stored by the server, and Admin-only batch deletion (one audited job per
/// profile, behind a confirm token, the typed workstation name and a reason). Fakes only: no PC is contacted and no profile is deleted.
/// </summary>
public sealed class ProfileManagementTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    private sealed record Envelope(bool? Unavailable, string? Reason, List<ProfileRowDto> Items, DateTime? LastKnownUtc);
    private sealed record Refusal(string Error, List<SidReason>? Reasons);

    private static string UserSid(int n) => $"S-1-5-21-1-2-3-{n}";

    private static ProfileInfo P(int n, string? account = null, bool loaded = false, bool special = false, string? path = null, DateTime? last = null, string? source = null) =>
        new(UserSid(n), account ?? $@"TEST\user{n}", path ?? $@"C:\Users\user{n}", last, loaded, special, false, null, source);

    private static void Reset()
    {
        FakeCollector.ProfilesOverride = null;
        FakeCollector.Failing.Clear();
        FakeProfileCollector.Users = null;
        FakeProfileCollector.Fail = false;
    }

    private async Task<(Guid Id, string Host)> SeedAsync(string? os = null, bool online = true)
    {
        var id = await factory.SeedEndpointAsync("PC-F3", online);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
        var e = db.Endpoints.Single(x => x.Id == id);
        if (os is not null) e.OsCaption = os;
        await db.SaveChangesAsync();
        return (id, e.Hostname);
    }

    private async Task<Envelope> ListAsync(HttpClient c, Guid id) => (await c.GetFromJsonAsync<Envelope>($"/api/endpoints/{id}/profiles"))!;

    private async Task StoreSizeAsync(Guid endpoint, string sid, long bytes, DateTime? measured = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
        db.ProfileSizes.Add(new ProfileSize { Id = Guid.NewGuid(), EndpointId = endpoint, Sid = sid, SizeBytes = bytes, MeasuredUtc = measured ?? DateTime.UtcNow.AddHours(-1), JobId = Guid.NewGuid(), MeasuredBySid = "S-1-5-21-1-2-3-1000" });
        await db.SaveChangesAsync();
    }

    // ================================================================ list

    [Fact]
    public async Task System_default_and_non_user_profiles_are_never_returned()
    {
        try
        {
            var (id, _) = await SeedAsync();
            FakeCollector.ProfilesOverride =
            [
                new ProfileInfo("S-1-5-18", null, @"C:\Windows\system32\config\systemprofile", null, false, false, false, null),
                new ProfileInfo("S-1-5-19", null, @"C:\Windows\ServiceProfiles\LocalService", null, false, false, false, null),
                new ProfileInfo("S-1-5-20", null, @"C:\Windows\ServiceProfiles\NetworkService", null, false, false, false, null),
                new ProfileInfo(UserSid(1), @"TEST\flagged", @"C:\Users\flagged", null, false, true, false, null),
                new ProfileInfo(UserSid(2), @"TEST\Default", @"C:\Users\Default", null, false, false, false, null),
                new ProfileInfo(UserSid(3), @"TEST\Public", @"C:\Users\Public", null, false, false, false, null),
                new ProfileInfo("S-1-5-80-1-2-3-4-5", null, @"C:\Users\svc", null, false, false, false, null),
                new ProfileInfo("S-1-5-21-1-2-3-500", @"TEST\RenamedAdmin", @"C:\Users\RenamedAdmin", null, false, false, false, null),
                new ProfileInfo("S-1-5-21-1-2-3-503", @"TEST\DefaultAccount", @"C:\Users\DefaultAccount", null, false, false, false, null),
                P(10), P(11),
            ];
            var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);

            var raw = await (await op.GetAsync($"/api/endpoints/{id}/profiles")).Content.ReadAsStringAsync();
            var list = JsonSerializer.Deserialize<Envelope>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

            list.Items.Select(i => i.Sid).Should().BeEquivalentTo([UserSid(10), UserSid(11)]);
            raw.Should().NotContain("S-1-5-18").And.NotContain("systemprofile").And.NotContain("flagged").And.NotContain("Public");
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task Viewers_cannot_list_and_anonymous_callers_are_challenged()
    {
        var (id, _) = await SeedAsync();
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        (await viewer.GetAsync($"/api/endpoints/{id}/profiles")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await factory.ClientFor(null).GetAsync($"/api/endpoints/{id}/profiles")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Rows_say_server_side_whether_they_can_be_deleted_and_why_not()
    {
        try
        {
            var (id, _) = await SeedAsync();
            var adminSid = TestIdentities.NewSid();
            FakeCollector.ProfilesOverride =
            [
                P(1),                                                               // fine
                P(2, loaded: true),                                                 // profile loaded
                P(3),                                                               // signed in (interactive session)
                P(4, account: ""),                                                  // account does not resolve
                new ProfileInfo(adminSid, @"TEST\me", @"C:\Users\me", null, false, false, false, null),   // the caller's own
                P(6, path: @"D:\Profiles\user6"),                                   // not under X:\Users
            ];
            FakeProfileCollector.Users = new SignedInUsers([UserSid(3)], []);
            var admin = await factory.UserClientAsync(adminSid, Role.Admin);
            var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);

            var asAdmin = (await ListAsync(admin, id)).Items.ToDictionary(i => i.Sid);
            asAdmin[UserSid(1)].Should().Match<ProfileRowDto>(r => r.Deletable && r.BlockReason == null);
            asAdmin[UserSid(2)].Should().Match<ProfileRowDto>(r => !r.Deletable && r.Loaded && r.BlockReason!.Contains("signed in"));
            asAdmin[UserSid(3)].Should().Match<ProfileRowDto>(r => !r.Deletable && r.BlockReason!.Contains("signed in"));
            asAdmin[UserSid(4)].Should().Match<ProfileRowDto>(r => !r.Deletable && r.Account == null && r.BlockReason!.Contains("could not be verified"));
            asAdmin[adminSid].Should().Match<ProfileRowDto>(r => !r.Deletable && r.BlockReason!.Contains("your own"));
            asAdmin[UserSid(6)].Should().Match<ProfileRowDto>(r => !r.Deletable && r.BlockReason!.Contains("X:\\Users"));

            var asOperator = (await ListAsync(op, id)).Items.ToDictionary(i => i.Sid);
            asOperator[UserSid(1)].Deletable.Should().BeFalse("only an Admin may delete");
            asOperator[UserSid(1)].BlockReason.Should().Contain("Admin");
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task When_who_is_signed_in_cannot_be_read_no_row_is_deletable()
    {
        try
        {
            var (id, _) = await SeedAsync();
            FakeCollector.ProfilesOverride = [P(1), P(2)];
            FakeProfileCollector.Fail = true;
            var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);

            var list = await ListAsync(admin, id);

            list.Unavailable.Should().BeNull("the profiles were read; only the sign-in check failed");
            list.Items.Should().HaveCount(2).And.OnlyContain(r => !r.Deletable && r.BlockReason == "could not verify who is signed in");
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task Rows_on_a_protected_workstation_are_not_deletable()
    {
        try
        {
            var (id, _) = await SeedAsync(os: "Microsoft Windows Server 2022 Standard");
            FakeCollector.ProfilesOverride = [P(1)];
            var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);

            var row = (await ListAsync(admin, id)).Items.Single();

            row.Deletable.Should().BeFalse();
            row.BlockReason.Should().Contain("Protected workstation").And.Contain("server OS");
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task Last_used_and_its_source_are_returned_and_the_source_is_null_when_unknown()
    {
        try
        {
            var (id, _) = await SeedAsync();
            var when = new DateTime(2026, 3, 1, 9, 30, 0, DateTimeKind.Utc);
            FakeCollector.ProfilesOverride = [P(1, last: when, source: "ProfileList"), P(2, last: when.AddDays(1), source: "ProfileList"), P(3, last: when.AddDays(2), source: "Wmi"), P(4)];
            var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);

            var rows = (await ListAsync(op, id)).Items.ToDictionary(i => i.Sid);

            rows[UserSid(1)].LastUsedSource.Should().Be("ProfileList");
            rows[UserSid(1)].LastUsedUtc.Should().Be(when);
            rows[UserSid(2)].LastUsedSource.Should().Be("ProfileList");
            rows[UserSid(3)].LastUsedSource.Should().Be("Wmi");
            rows[UserSid(4)].Should().Match<ProfileRowDto>(r => r.LastUsedUtc == null && r.LastUsedSource == null);
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task An_offline_workstation_is_a_200_envelope_with_the_last_known_rows_switched_off()
    {
        try
        {
            var (id, _) = await SeedAsync();
            FakeCollector.ProfilesOverride = [P(1), P(2)];
            var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
            (await ListAsync(admin, id)).Items.Should().HaveCount(2).And.Contain(r => r.Deletable);

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
                db.Endpoints.Single(e => e.Id == id).Online = false;
                await db.SaveChangesAsync();
            }

            var response = await admin.GetAsync($"/api/endpoints/{id}/profiles");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var list = (await response.Content.ReadFromJsonAsync<Envelope>())!;
            list.Unavailable.Should().BeTrue();
            list.Reason.Should().Be(ProfileQuery.Offline);
            list.LastKnownUtc.Should().NotBeNull();
            list.Items.Should().HaveCount(2).And.OnlyContain(r => !r.Deletable && r.BlockReason!.Contains("Last known"));
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task An_offline_workstation_that_was_never_read_has_an_empty_envelope()
    {
        var (id, _) = await SeedAsync(online: false);
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);

        var response = await op.GetAsync($"/api/endpoints/{id}/profiles");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = (await response.Content.ReadFromJsonAsync<Envelope>())!;
        list.Unavailable.Should().BeTrue();
        list.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_read_that_fails_is_a_generic_unavailable_envelope_never_a_500_or_raw_text()
    {
        try
        {
            var (id, host) = await SeedAsync();
            FakeCollector.Failing.Add(host);
            var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);

            var response = await op.GetAsync($"/api/endpoints/{id}/profiles");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var raw = await response.Content.ReadAsStringAsync();
            raw.Should().Contain(ProfileReader.ReadFailed).And.NotContain("secret-detail").And.NotContain("10.99.99.99").And.NotContain("internal-share");
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task Unknown_workstation_is_404()
    {
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        (await op.GetAsync($"/api/endpoints/{Guid.NewGuid()}/profiles")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_list_shows_the_stored_size_and_when_it_was_measured_and_nothing_else_can_set_it()
    {
        try
        {
            var (id, _) = await SeedAsync();
            FakeCollector.ProfilesOverride = [P(1), P(2)];
            var measured = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
            await StoreSizeAsync(id, UserSid(1), 4_200_000_000, measured);
            var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);

            var rows = (await ListAsync(op, id)).Items.ToDictionary(i => i.Sid);

            rows[UserSid(1)].SizeBytes.Should().Be(4_200_000_000);
            rows[UserSid(1)].SizeMeasuredUtc.Should().Be(measured);
            rows[UserSid(2)].Should().Match<ProfileRowDto>(r => r.SizeBytes == null && r.SizeMeasuredUtc == null);

            // There is no route that accepts a size from a client.
            foreach (var path in new[] { $"/api/endpoints/{id}/profiles", $"/api/endpoints/{id}/profiles/sizes" })
            {
                var put = await op.PostAsJsonAsync(path, new { sid = UserSid(2), sizeBytes = 1 });
                put.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
            }
            (await ListAsync(op, id)).Items.Single(r => r.Sid == UserSid(2)).SizeBytes.Should().BeNull();
        }
        finally { Reset(); }
    }

    // ================================================================ sizes (on demand, stored by the server)

    private async Task<JobTargetInfo> StartSizesJobAsync(HttpClient c, Guid pc, object? parameters = null)
    {
        var created = await c.PostAsJsonAsync("/api/jobs", new { actionCode = "profile.sizes", endpointIds = new[] { pc }, parameters });
        created.StatusCode.Should().Be(HttpStatusCode.Accepted, await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<JobDetail>())!.Targets.Single();
    }

    private async Task FinishAsync(Guid target, TargetState state, params string[] lines)
    {
        using var scope = factory.Services.CreateScope();
        var reports = scope.ServiceProvider.GetRequiredService<IJobReports>();
        if (lines.Length > 0) await reports.LogAsync(target, "stdout", lines, null);
        await reports.CompletedAsync(target, state, state == TargetState.Succeeded ? 0 : 1, "done", null);
    }

    private static string Line(string sid, long bytes, bool partial = false) =>
        JsonSerializer.Serialize(new { sid, path = @"C:\Users\x", bytes, files = 3, errors = 0, partial });

    [Fact]
    public async Task A_finished_sizes_job_is_stored_by_the_server_and_read_back_through_the_list()
    {
        try
        {
            var (pc, _) = await SeedAsync();
            FakeCollector.ProfilesOverride = [P(1), P(2), P(3)];
            var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);

            var target = await StartSizesJobAsync(op, pc);   // {} = every profile
            (await ListAsync(op, pc)).Items.Should().OnlyContain(r => r.SizeBytes == null, "sizes are never computed on page load");
            await FinishAsync(target.Id, TargetState.Succeeded, Line(UserSid(1), 1_000), Line(UserSid(2), 2_000), Line(UserSid(3), 3_000, partial: true), Line("S-1-5-18", 9));

            using (var scope = factory.Services.CreateScope())   // the list caches reads for a few seconds; the stored sizes are read fresh each time
                scope.ServiceProvider.GetRequiredService<INexoraDb>().ProfileSizes.Where(s => s.EndpointId == pc).Select(s => s.Sid).OrderBy(s => s).ToList()
                    .Should().Equal(UserSid(1), UserSid(2));
            var rows = (await ListAsync(op, pc)).Items.ToDictionary(i => i.Sid);
            rows[UserSid(1)].SizeBytes.Should().Be(1_000);
            rows[UserSid(2)].SizeBytes.Should().Be(2_000);
            rows[UserSid(3)].SizeBytes.Should().BeNull("a partial measurement understates the size and is not stored");
            rows[UserSid(1)].SizeMeasuredUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(30));
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task A_newer_measurement_replaces_the_old_one_and_a_failed_job_stores_nothing()
    {
        try
        {
            var (pc, _) = await SeedAsync();
            var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);

            await FinishAsync((await StartSizesJobAsync(op, pc)).Id, TargetState.Succeeded, Line(UserSid(1), 100));
            await FinishAsync((await StartSizesJobAsync(op, pc)).Id, TargetState.Failed, Line(UserSid(1), 999_999));
            factory.Query(db => db.ProfileSizes.Single(s => s.EndpointId == pc).SizeBytes).Should().Be(100);

            var second = await StartSizesJobAsync(op, pc);
            await FinishAsync(second.Id, TargetState.Succeeded, Line(UserSid(1), 555));
            var row = factory.Query(db => db.ProfileSizes.Single(s => s.EndpointId == pc));
            row.SizeBytes.Should().Be(555);
            row.JobId.Should().Be(second.JobId);
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task A_single_profile_job_stores_only_that_profile()
    {
        try
        {
            var (pc, _) = await SeedAsync();
            var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);

            var target = await StartSizesJobAsync(op, pc, new { sid = UserSid(2) });
            await FinishAsync(target.Id, TargetState.Succeeded, Line(UserSid(1), 1), Line(UserSid(2), 2));

            factory.Query(db => db.ProfileSizes.Where(s => s.EndpointId == pc).Select(s => s.Sid).ToList()).Should().Equal(UserSid(2));
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task A_client_cannot_hand_a_size_to_any_job()
    {
        var (pc, _) = await SeedAsync();
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);

        var sizes = await op.PostAsJsonAsync("/api/jobs", new { actionCode = "profile.sizes", endpointIds = new[] { pc }, parameters = new { sizeBytes = "1" } });
        sizes.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await sizes.Content.ReadAsStringAsync()).Should().Contain("sizeBytes");
        ActionCatalog.Find("profile.delete")!.Parameters.Should().NotContain(p => p.Name == "sizeBytes");
        factory.Query(db => db.ProfileSizes.Count(s => s.EndpointId == pc)).Should().Be(0);
        _ = admin;
    }

    [Fact]
    public async Task Measuring_sizes_needs_the_operator_role()
    {
        var (pc, _) = await SeedAsync();
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        (await viewer.PostAsJsonAsync("/api/jobs", new { actionCode = "profile.sizes", endpointIds = new[] { pc } })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ================================================================ batch deletion

    private sealed record Context(HttpClient Admin, string AdminSid, Guid Pc, string Host);

    private async Task<Context> BatchSetupAsync(string? os = null, params ProfileInfo[] profiles)
    {
        var (pc, host) = await SeedAsync(os);
        FakeCollector.ProfilesOverride = profiles.Length == 0 ? [P(1), P(2), P(3)] : profiles;
        var sid = TestIdentities.NewSid();
        return new Context(await factory.UserClientAsync(sid, Role.Admin), sid, pc, host);
    }

    private static Task<HttpResponseMessage> Confirm(HttpClient c, Guid pc, IEnumerable<string>? sids) =>
        c.PostAsJsonAsync($"/api/endpoints/{pc}/profiles/delete-batch/confirm", new { sids });

    private static Task<HttpResponseMessage> Delete(HttpClient c, Guid pc, IEnumerable<string>? sids, string? token, string? typed, string? reason = "left the bank") =>
        c.PostAsJsonAsync($"/api/endpoints/{pc}/profiles/delete-batch", new { sids, token, typed, reason });

    private static async Task<ProfileBatchConfirmResponse> Confirmed(HttpClient c, Guid pc, params string[] sids)
    {
        var r = await Confirm(c, pc, sids);
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<ProfileBatchConfirmResponse>())!;
    }

    /// <summary>Batch jobs that target this workstation (the database is shared by all tests, so counts are always per workstation).</summary>
    private int BatchJobs(Guid pc) => factory.Query(db => db.JobTargets.Count(t => t.EndpointId == pc && db.Jobs.Any(j => j.Id == t.JobId && j.BatchId != null)));

    [Fact]
    public async Task Confirm_re_reads_the_PC_and_returns_a_token_the_hostname_and_the_profiles_with_server_sizes()
    {
        try
        {
            var ctx = await BatchSetupAsync();
            await StoreSizeAsync(ctx.Pc, UserSid(2), 2_222);

            var confirmed = await Confirmed(ctx.Admin, ctx.Pc, UserSid(3), UserSid(2));

            confirmed.Token.Should().NotBeNullOrEmpty();
            confirmed.ExpectedHostname.Should().Be(ctx.Host);
            confirmed.Count.Should().Be(2);
            confirmed.Items.Select(i => i.Sid).Should().Equal([UserSid(2), UserSid(3)], "items are sorted by SID, and jobIds follow the same order");
            confirmed.Items[0].Should().Be(new ProfileBatchItem(UserSid(2), @"TEST\user2", @"C:\Users\user2", 2_222));
            confirmed.Items[1].SizeBytes.Should().BeNull("never measured");
            BatchJobs(ctx.Pc).Should().Be(0, "confirming creates nothing");
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task Operators_and_viewers_cannot_confirm_or_delete_in_bulk_and_no_job_appears()
    {
        try
        {
            var ctx = await BatchSetupAsync();
            var opSid = TestIdentities.NewSid();
            var viewerSid = TestIdentities.NewSid();
            var op = await factory.UserClientAsync(opSid, Role.Operator);
            var viewer = await factory.UserClientAsync(viewerSid, Role.Viewer);
            var token = (await Confirmed(ctx.Admin, ctx.Pc, UserSid(1))).Token;
            var before = BatchJobs(ctx.Pc);

            foreach (var who in new[] { op, viewer })
            {
                (await Confirm(who, ctx.Pc, [UserSid(1)])).StatusCode.Should().Be(HttpStatusCode.Forbidden);
                (await Delete(who, ctx.Pc, [UserSid(1)], token, ctx.Host)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            }
            (await Confirm(factory.ClientFor(null), ctx.Pc, [UserSid(1)])).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            BatchJobs(ctx.Pc).Should().Be(before);
            factory.Query(db => db.AuditEntries.Count(a => a.Action == "job.denied" && a.ActorSid == opSid && a.Details!.Contains("profile-batch"))).Should().Be(2, "an Operator is refused and audited by the service");
            factory.Query(db => db.AuditEntries.Count(a => a.Action == "job.denied" && a.ActorSid == viewerSid)).Should().Be(0, "a Viewer never reaches the service; the policy answers 403");
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task A_batch_call_without_the_antiforgery_header_is_rejected()
    {
        var ctx = await BatchSetupAsync();
        var bare = factory.ClientFor(ctx.AdminSid);   // signed in, but no X-XSRF-TOKEN
        (await Confirm(bare, ctx.Pc, [UserSid(1)])).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Delete(bare, ctx.Pc, [UserSid(1)], "x", ctx.Host)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Reset();
    }

    [Fact]
    public async Task Any_profile_that_fails_fails_the_whole_confirm_with_a_reason_per_SID_and_the_denial_is_audited()
    {
        try
        {
            var ctx = await BatchSetupAsync(null, P(1), P(2, loaded: true), P(3, account: ""), P(4), new ProfileInfo("S-1-5-18", null, @"C:\Windows", null, false, false, false, null));
            FakeProfileCollector.Users = new SignedInUsers([UserSid(4)], []);
            var denials = factory.Query(db => db.AuditEntries.Count(a => a.Action == "job.denied" && a.ActorSid == ctx.AdminSid));

            var r = await Confirm(ctx.Admin, ctx.Pc, [UserSid(1), UserSid(2), UserSid(3), UserSid(4), UserSid(5), "S-1-5-21-1-2-3-9999"]);

            r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var refusal = (await r.Content.ReadFromJsonAsync<Refusal>())!;
            refusal.Reasons.Should().NotBeNull();
            refusal.Reasons!.Select(x => x.Sid).Should().BeEquivalentTo([UserSid(2), UserSid(3), UserSid(4), UserSid(5), "S-1-5-21-1-2-3-9999"]);
            factory.Query(db => db.AuditEntries.Count(a => a.Action == "job.denied" && a.ActorSid == ctx.AdminSid)).Should().Be(denials + 1, "the refusal is audited");
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task A_PC_whose_sign_in_state_cannot_be_read_gets_no_token()
    {
        try
        {
            var ctx = await BatchSetupAsync();
            FakeProfileCollector.Fail = true;

            var r = await Confirm(ctx.Admin, ctx.Pc, [UserSid(1)]);

            r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var body = await r.Content.ReadAsStringAsync();
            body.Should().Contain("ould not verify who is signed in").And.NotContain("token\":\"").And.NotContain("secret-detail");
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task A_PC_that_cannot_be_read_at_all_is_a_503_with_a_generic_message()
    {
        try
        {
            var ctx = await BatchSetupAsync();
            FakeCollector.Failing.Add(ctx.Host);

            var r = await Confirm(ctx.Admin, ctx.Pc, [UserSid(1)]);

            r.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await r.Content.ReadAsStringAsync()).Should().NotContain("secret-detail").And.NotContain("10.99.99.99");
            factory.Query(db => db.AuditEntries.Count(a => a.Action == "job.denied" && a.ActorSid == ctx.AdminSid && a.Details!.Contains("Unavailable"))).Should().BeGreaterThan(0, "an unreadable PC refusal is audited");
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task A_protected_workstation_is_refused_for_confirm_and_delete()
    {
        try
        {
            var ctx = await BatchSetupAsync("Microsoft Windows Server 2022 Standard");
            var r = await Confirm(ctx.Admin, ctx.Pc, [UserSid(1)]);
            r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await r.Content.ReadAsStringAsync()).Should().Contain("Protected workstation");
            (await Delete(ctx.Admin, ctx.Pc, [UserSid(1)], "any", ctx.Host)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

            // The Nexora server itself too.
            var server = factory.Query(db => db.Endpoints.Single(e => e.IsPinned));
            (await Confirm(ctx.Admin, server.Id, [UserSid(1)])).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            BatchJobs(ctx.Pc).Should().Be(0); BatchJobs(server.Id).Should().Be(0);
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task More_than_50_profiles_are_refused_and_duplicates_are_deduplicated()
    {
        try
        {
            var many = Enumerable.Range(1, 51).Select(i => P(i)).ToArray();
            var ctx = await BatchSetupAsync(null, many);

            var tooMany = await Confirm(ctx.Admin, ctx.Pc, many.Select(p => p.Sid));
            tooMany.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await tooMany.Content.ReadAsStringAsync()).Should().Contain("At most 50");

            var fifty = await Confirm(ctx.Admin, ctx.Pc, many.Take(50).Select(p => p.Sid));
            fifty.StatusCode.Should().Be(HttpStatusCode.OK);

            var duplicates = await Confirmed(ctx.Admin, ctx.Pc, UserSid(1), UserSid(1), UserSid(2), UserSid(1).ToLowerInvariant(), " " + UserSid(2) + " ");
            duplicates.Count.Should().Be(2);
            duplicates.Items.Select(i => i.Sid).Should().Equal([UserSid(1), UserSid(2)]);
        }
        finally { Reset(); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-21-1-2-3-4001'; DROP TABLE jobs;--")]
    [InlineData("not-a-sid")]
    [InlineData("S-1-5-21-1-2-3-4001\n")]
    public async Task Empty_and_malformed_SIDs_are_refused_before_the_PC_is_asked(string? sid)
    {
        try
        {
            var ctx = await BatchSetupAsync();
            (await Confirm(ctx.Admin, ctx.Pc, sid is null ? null : [sid])).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task Bulk_delete_never_creates_a_job_without_a_valid_token_the_typed_hostname_and_a_reason()
    {
        try
        {
            var ctx = await BatchSetupAsync();
            var sids = new[] { UserSid(1), UserSid(2) };
            var good = await Confirmed(ctx.Admin, ctx.Pc, sids);
            var before = BatchJobs(ctx.Pc);

            (await Delete(ctx.Admin, ctx.Pc, sids, null, ctx.Host)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "no token");
            (await Delete(ctx.Admin, ctx.Pc, sids, "garbage.token", ctx.Host)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "invalid token");
            (await Delete(ctx.Admin, ctx.Pc, sids, good.Token, "old.user")).StatusCode.Should().Be(HttpStatusCode.BadRequest, "the account is not the workstation name");
            (await Delete(ctx.Admin, ctx.Pc, sids, good.Token, null)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "nothing typed");
            (await Delete(ctx.Admin, ctx.Pc, sids, good.Token, "2")).StatusCode.Should().Be(HttpStatusCode.BadRequest, "the count is not the workstation name");
            (await Delete(ctx.Admin, ctx.Pc, sids, good.Token, ctx.Host, reason: null)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "reason required");
            (await Delete(ctx.Admin, ctx.Pc, sids, good.Token, ctx.Host, reason: "   ")).StatusCode.Should().Be(HttpStatusCode.BadRequest, "blank reason");
            (await Delete(ctx.Admin, ctx.Pc, sids, good.Token, ctx.Host, reason: new string('x', 501))).StatusCode.Should().Be(HttpStatusCode.BadRequest, "reason too long");
            BatchJobs(ctx.Pc).Should().Be(before);

            // Failed attempts did not burn the token; the right one now works, exactly once.
            var ok = await Delete(ctx.Admin, ctx.Pc, sids, good.Token, ctx.Host);
            ok.StatusCode.Should().Be(HttpStatusCode.Accepted, await ok.Content.ReadAsStringAsync());
            // While that batch is active a second one is a conflict; once its jobs are finished the spent token is simply invalid.
            (await Delete(ctx.Admin, ctx.Pc, sids, good.Token, ctx.Host)).StatusCode.Should().Be(HttpStatusCode.Conflict);
            var created = (await ok.Content.ReadFromJsonAsync<ProfileBatchCreated>())!;
            var until = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < until && factory.Query(db => db.JobTargets.Where(t => created.JobIds.Contains(t.JobId)).ToList()).Any(t => !JobService.IsFinal(t.Status))) await Task.Delay(200);
            (await Delete(ctx.Admin, ctx.Pc, sids, good.Token, ctx.Host)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "single use");
            BatchJobs(ctx.Pc).Should().Be(before + 2);
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task A_token_is_bound_to_the_sids_the_workstation_and_the_operator()
    {
        try
        {
            var ctx = await BatchSetupAsync();
            var other = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
            var otherPc = await SeedAsync();
            var token = (await Confirmed(ctx.Admin, ctx.Pc, UserSid(1))).Token;
            var before = BatchJobs(ctx.Pc);

            (await Delete(ctx.Admin, ctx.Pc, [UserSid(1), UserSid(2)], token, ctx.Host)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "another set of SIDs");
            (await Delete(ctx.Admin, ctx.Pc, [UserSid(2)], token, ctx.Host)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "another SID");
            (await Delete(other, ctx.Pc, [UserSid(1)], token, ctx.Host)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "another operator");
            (await Delete(ctx.Admin, otherPc.Id, [UserSid(1)], token, otherPc.Host)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "another workstation");
            BatchJobs(ctx.Pc).Should().Be(before);

            (await Delete(ctx.Admin, ctx.Pc, [UserSid(1)], token, ctx.Host)).StatusCode.Should().Be(HttpStatusCode.Accepted, "the same SIDs in the same request still work");
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task Delete_re_verifies_at_that_moment_and_a_profile_that_became_loaded_fails_the_whole_batch()
    {
        try
        {
            var ctx = await BatchSetupAsync();
            var sids = new[] { UserSid(1), UserSid(2) };
            var token = (await Confirmed(ctx.Admin, ctx.Pc, sids)).Token;

            FakeCollector.ProfilesOverride = [P(1), P(2, loaded: true), P(3)];   // the user signed in after the confirmation
            var r = await Delete(ctx.Admin, ctx.Pc, sids, token, ctx.Host);

            r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await r.Content.ReadFromJsonAsync<Refusal>())!.Reasons.Should().ContainSingle().Which.Sid.Should().Be(UserSid(2));
            BatchJobs(ctx.Pc).Should().Be(0);
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task One_job_and_one_audit_row_per_profile_with_sid_account_path_and_the_servers_size_all_in_one_batch()
    {
        try
        {
            var ctx = await BatchSetupAsync();
            await StoreSizeAsync(ctx.Pc, UserSid(1), 1_500_000_000);
            var sids = new[] { UserSid(2), UserSid(1) };
            var confirmed = await Confirmed(ctx.Admin, ctx.Pc, sids);

            // Extra fields a client might add (sizes, a different list of items) are not part of the contract and change nothing.
            var response = await ctx.Admin.PostAsJsonAsync($"/api/endpoints/{ctx.Pc}/profiles/delete-batch",
                new { sids, token = confirmed.Token, typed = ctx.Host, reason = "left the bank", sizeBytes = 123, items = new[] { new { sid = UserSid(1), sizeBytes = 123456 } } });
            response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
            var created = (await response.Content.ReadFromJsonAsync<ProfileBatchCreated>())!;

            created.JobIds.Should().HaveCount(2).And.OnlyHaveUniqueItems();
            var jobs = factory.Query(db => db.Jobs.Where(j => j.BatchId == created.BatchId).ToList());
            jobs.Should().HaveCount(2).And.OnlyContain(j => j.ActionCode == "profile.delete" && j.InitiatorSid == ctx.AdminSid && j.TargetCount == 1 && j.Reason == "left the bank");
            // jobIds are in the order of the confirm response's items
            created.JobIds.Select(id => JsonSerializer.Deserialize<Dictionary<string, string>>(jobs.Single(j => j.Id == id).ParametersJson)!["sid"])
                .Should().Equal(confirmed.Items.Select(i => i.Sid));

            var audit = factory.Query(db => db.AuditEntries.Where(a => a.Action == "job.create" && a.ActorSid == ctx.AdminSid).ToList());
            audit.Should().HaveCount(2);
            foreach (var sid in sids)
            {
                var job = jobs.Single(j => j.ParametersJson.Contains(sid));
                var row = audit.Single(a => a.TargetId == job.Id.ToString());
                row.Details.Should().Contain(sid).And.Contain($"user{sid[^1]}").And.Contain(@"C:\\Users\\user" + sid[^1]).And.Contain(created.BatchId.ToString());
                if (sid == UserSid(1)) row.Details.Should().Contain("1500000000").And.NotContain("123456").And.NotContain(":123,");
                else row.Details.Should().Contain("\"sizeBytes\":null");
            }
            factory.Query(db => db.JobTargets.Count(t => created.JobIds.Contains(t.JobId))).Should().Be(2);
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task A_second_batch_is_refused_while_one_is_still_queued_or_running_on_the_same_workstation()
    {
        try
        {
            var ctx = await BatchSetupAsync();
            var first = await Confirmed(ctx.Admin, ctx.Pc, UserSid(1));
            (await Delete(ctx.Admin, ctx.Pc, [UserSid(1)], first.Token, ctx.Host)).StatusCode.Should().Be(HttpStatusCode.Accepted);

            var second = await Confirm(ctx.Admin, ctx.Pc, [UserSid(2)]);

            second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task A_batch_runs_one_profile_at_a_time_and_a_failure_does_not_stop_the_rest()
    {
        try
        {
            var ctx = await BatchSetupAsync();
            var sids = new[] { UserSid(1), UserSid(2), UserSid(3) };
            var token = (await Confirmed(ctx.Admin, ctx.Pc, sids)).Token;
            var created = (await (await Delete(ctx.Admin, ctx.Pc, sids, token, ctx.Host)).Content.ReadFromJsonAsync<ProfileBatchCreated>())!;

            // The first profile fails (reported by the helper); the others must still be run.
            var failedTarget = factory.Query(db => db.JobTargets.Single(t => t.JobId == created.JobIds[0]).Id);
            using (var scope = factory.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<IJobReports>().CompletedAsync(failedTarget, TargetState.Failed, 1, "Refused: simulated failure", null);

            List<(Guid JobId, TargetState Status, DateTime? Started, DateTime? Finished)> Targets() =>
                factory.Query(db => db.JobTargets.Where(t => created.JobIds.Contains(t.JobId)).ToList()).Select(t => (t.JobId, t.Status, t.StartedUtc, t.FinishedUtc)).ToList();
            var until = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < until && !(Targets().Count == 3 && Targets().All(t => JobService.IsFinal(t.Status)))) await Task.Delay(200);

            var done = Targets().OrderBy(t => created.JobIds.ToList().IndexOf(t.JobId)).ToList();
            done.Should().HaveCount(3);
            done[0].Status.Should().Be(TargetState.Failed);
            done[1].Status.Should().Be(TargetState.Succeeded, "the batch continues after a failure");
            done[2].Status.Should().Be(TargetState.Succeeded);
            done[2].Started.Should().BeOnOrAfter(done[1].Finished!.Value, "one profile at a time on a PC");
            factory.Query(db => db.AuditEntries.Count(a => a.Action == "job.target" && a.ActorSid == ctx.AdminSid && a.TargetId == ctx.Host)).Should().BeGreaterThanOrEqualTo(3);
            factory.Query(db => db.AuditEntries.Where(a => a.Action == "job.target" && a.ActorSid == ctx.AdminSid).ToList())
                .Should().Contain(a => !a.Success && a.Details!.Contains("simulated failure") && a.Details.Contains(created.BatchId.ToString()), "failures are audited too");
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task Confirm_and_delete_are_rate_limited_per_operator()
    {
        try
        {
            var ctx = await BatchSetupAsync();
            var codes = new List<HttpStatusCode>();
            for (var i = 0; i < 22; i++) codes.Add((await Confirm(ctx.Admin, ctx.Pc, null)).StatusCode);   // each is a 400, but each counts

            codes.Take(20).Should().OnlyContain(c => c == HttpStatusCode.BadRequest);
            codes.Skip(20).Should().OnlyContain(c => c == HttpStatusCode.TooManyRequests);
            var another = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
            (await Confirm(another, ctx.Pc, null)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "the limit is per operator");
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task Refused_batch_requests_are_audited_as_denials_with_the_stage()
    {
        try
        {
            var ctx = await BatchSetupAsync();
            var before = factory.Query(db => db.AuditEntries.Count(a => a.Action == "job.denied" && a.ActorSid == ctx.AdminSid));
            var token = (await Confirmed(ctx.Admin, ctx.Pc, UserSid(1))).Token;

            (await Delete(ctx.Admin, ctx.Pc, [UserSid(1)], token, "wrong-host")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await Delete(ctx.Admin, ctx.Pc, [UserSid(1)], "bad", ctx.Host)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await Confirm(ctx.Admin, ctx.Pc, [UserSid(1), "S-1-5-21-1-2-3-9999"])).StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var rows = factory.Query(db => db.AuditEntries.Where(a => a.Action == "job.denied" && a.ActorSid == ctx.AdminSid).OrderBy(a => a.Id).ToList());
            rows.Should().HaveCount(before + 3);
            rows.TakeLast(3).Should().OnlyContain(a => !a.Success && a.Details!.Contains("profile-batch") && a.Details.Contains("profile.delete"));
            rows.Last().Details.Should().Contain("S-1-5-21-1-2-3-9999");
        }
        finally { Reset(); }
    }
}
