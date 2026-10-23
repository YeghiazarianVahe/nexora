using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Nexora.Application;
using Nexora.Domain;

namespace Nexora.Api.Tests;

public sealed class MeAndHealthTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    [Fact]
    public async Task Health_is_reachable_anonymously()
    {
        var response = await factory.ClientFor(null).GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Me_requires_authentication()
    {
        var response = await factory.ClientFor(null).GetAsync("/api/me");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_for_group_member_without_a_record_reports_no_access()
    {
        var sid = TestIdentities.NewSid();
        var me = await factory.ClientFor(sid, "TEST\\bob").GetFromJsonAsync<MeDto>("/api/me");

        me!.Sid.Should().Be(sid);
        me.Account.Should().Be("TEST\\bob");
        me.DisplayName.Should().Be("TEST\\bob"); // falls back to account when AD has no answer
        me.Role.Should().Be("None");
        me.IsSuperAdmin.Should().BeFalse();
        me.Status.Should().Be("NoAccess");
    }

    [Fact]
    public async Task Me_for_a_non_member_reports_denied()
    {
        var me = await factory.ClientFor(TestIdentities.NewSid(), inAccessGroup: false).GetFromJsonAsync<MeDto>("/api/me");
        me!.Status.Should().Be("Denied");
    }

    [Fact]
    public async Task Me_identifies_super_admin_by_sid_not_account()
    {
        // Account label deliberately wrong: matching must be by SID only.
        var me = await factory.ClientFor(TestIdentities.SuperAdminSid, "WHATEVER\\someone").GetFromJsonAsync<MeDto>("/api/me");

        me!.IsSuperAdmin.Should().BeTrue();
        me.Role.Should().Be("SuperAdmin");
        me.DisplayName.Should().Be("Super Admin");
        me.Status.Should().Be("Active");
    }

    [Fact]
    public async Task Same_account_name_with_different_sid_is_not_super_admin()
    {
        var me = await factory.ClientFor(TestIdentities.NewSid(), "TEST\\SuperAdmin").GetFromJsonAsync<MeDto>("/api/me");
        me!.IsSuperAdmin.Should().BeFalse();
    }

    [Theory]
    [InlineData(Role.Viewer)]
    [InlineData(Role.Operator)]
    [InlineData(Role.Admin)]
    public async Task Me_reports_the_database_role(Role role)
    {
        var sid = TestIdentities.NewSid();
        await factory.SeedUserAsync(sid, role);
        var me = await factory.ClientFor(sid).GetFromJsonAsync<MeDto>("/api/me");

        me!.Role.Should().Be(role.ToString());
        me.Status.Should().Be("Active");
    }

    [Fact]
    public async Task Revoked_user_has_no_role()
    {
        var sid = TestIdentities.NewSid();
        await factory.SeedUserAsync(sid, Role.Admin, status: UserStatus.Revoked);
        var client = factory.ClientFor(sid);

        (await client.GetFromJsonAsync<MeDto>("/api/me"))!.Status.Should().Be("Revoked");
        (await client.GetAsync("/api/dashboard")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        var response = await factory.ClientFor(null).GetAsync("/health");

        response.Headers.GetValues("X-Frame-Options").Should().ContainSingle("DENY");
        response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle("nosniff");
        response.Headers.Contains("Content-Security-Policy").Should().BeTrue();
        response.Headers.Contains("Referrer-Policy").Should().BeTrue();
    }
}
