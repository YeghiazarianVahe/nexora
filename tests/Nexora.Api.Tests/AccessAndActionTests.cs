using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Nexora.Application;
using Nexora.Domain;

namespace Nexora.Api.Tests;

public sealed class AccessFlowTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    [Fact]
    public async Task Member_without_a_role_can_request_access_once()
    {
        var sid = TestIdentities.NewSid();
        var client = await factory.UserClientAsync(sid, role: null);

        var first = await client.PostAsync("/api/access/request", null);
        var second = await client.PostAsync("/api/access/request", null);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Query(db => db.AccessRequests.Count(r => r.Sid == sid)).Should().Be(1);
        (await client.GetFromJsonAsync<MeDto>("/api/me"))!.Status.Should().Be("Pending");
    }

    [Fact]
    public async Task Non_member_creates_no_access_request()
    {
        var sid = TestIdentities.NewSid();
        var client = await factory.UserClientAsync(sid, role: null, inAccessGroup: false);

        var response = await client.PostAsync("/api/access/request", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        factory.Query(db => db.AccessRequests.Count(r => r.Sid == sid)).Should().Be(0);
        factory.Query(db => db.Users.Count(u => u.Sid == sid)).Should().Be(0);
    }

    [Fact]
    public async Task User_without_a_role_reaches_only_me_and_the_access_endpoints()
    {
        var client = await factory.UserClientAsync(TestIdentities.NewSid(), role: null);

        (await client.GetAsync("/api/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/antiforgery")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync("/api/access/request", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (var url in new[] { "/api/dashboard", "/api/endpoints", "/api/audit", "/api/jobs", "/api/scripts", "/api/settings" })
            (await client.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Forbidden, url);
    }

    [Fact]
    public async Task User_without_a_role_can_open_the_realtime_channel_to_await_approval()
    {
        var client = await factory.UserClientAsync(TestIdentities.NewSid(), role: null);
        var response = await client.PostAsync("/hubs/nexora/negotiate?negotiateVersion=1", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Realtime_channel_requires_authentication()
    {
        var response = await factory.ClientFor(null).PostAsync("/hubs/nexora/negotiate?negotiateVersion=1", null);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Only_operators_can_open_the_helper_channel()
    {
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);

        (await viewer.PostAsync("/hubs/helper/negotiate?negotiateVersion=1", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await op.PostAsync("/hubs/helper/negotiate?negotiateVersion=1", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
