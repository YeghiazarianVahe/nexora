using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Nexora.Application;
using Nexora.Domain;

namespace Nexora.Api.Tests;

public sealed class QaRegressionTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    [Fact]
    public async Task Readdressing_rejects_case_insensitive_duplicates()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var first = await factory.SeedEndpointAsync("QA-DUP");
        var second = await factory.SeedEndpointAsync("QA-OTHER");
        var address = factory.Query(db => db.Endpoints.Single(e => e.Id == first).Hostname);
        var response = await admin.PutAsJsonAsync($"/api/endpoints/{second}", new { address = address.ToLowerInvariant() });
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        factory.Query(db => db.Endpoints.Single(e => e.Id == second).Hostname).Should().NotBe(address.ToLowerInvariant());
    }

    [Theory]
    [InlineData("/api/access/request")]
    [InlineData("/hubs/helper/negotiate?negotiateVersion=1")]
    public async Task Same_authority_different_scheme_is_cross_origin(string path)
    {
        var client = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Origin", "https://localhost");
        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"endpointIds\":null}")]
    public async Task Missing_member_list_does_not_clear_existing_members(string json)
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var id = await factory.SeedEndpointAsync();
        var group = (await (await admin.PostAsJsonAsync("/api/groups", new { name = "QA-" + Guid.NewGuid().ToString("N") })).Content.ReadFromJsonAsync<GroupDto>())!;
        (await admin.PutAsJsonAsync($"/api/groups/{group.Id}/members", new { endpointIds = new[] { id } })).EnsureSuccessStatusCode();
        using var body = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        (await admin.PutAsync($"/api/groups/{group.Id}/members", body)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        factory.Query(db => db.GroupMembers.Any(m => m.GroupId == group.Id && m.EndpointId == id)).Should().BeTrue();
        (await admin.PutAsJsonAsync($"/api/groups/{group.Id}/members", new { endpointIds = Array.Empty<Guid>() })).EnsureSuccessStatusCode();
        factory.Query(db => db.GroupMembers.Any(m => m.GroupId == group.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Individually_added_workstation_can_be_assigned_and_filtered_by_group()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        var pc = (await (await admin.PostAsJsonAsync("/api/endpoints", new { items = new[] { new { address = "QA-" + Guid.NewGuid().ToString("N")[..12] } } })).Content.ReadFromJsonAsync<List<EndpointSummaryDto>>())!.Single();
        var group = (await (await admin.PostAsJsonAsync("/api/groups", new { name = "QA-" + Guid.NewGuid().ToString("N") })).Content.ReadFromJsonAsync<GroupDto>())!;
        (await viewer.PutAsJsonAsync($"/api/groups/{group.Id}/members", new { endpointIds = new[] { pc.Id } })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.PutAsJsonAsync($"/api/groups/{group.Id}/members", new { endpointIds = new[] { pc.Id, pc.Id } })).EnsureSuccessStatusCode();
        var rows = await viewer.GetFromJsonAsync<List<EndpointSummaryDto>>($"/api/endpoints?groupId={group.Id}");
        rows!.Should().ContainSingle().Which.Id.Should().Be(pc.Id);
        factory.Query(db => db.GroupMembers.Count(m => m.GroupId == group.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Member_changes_are_a_delta_and_do_not_remove_members_added_meanwhile()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var operatorClient = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var a = await factory.SeedEndpointAsync();
        var b = await factory.SeedEndpointAsync();
        var c = await factory.SeedEndpointAsync();
        var group = (await (await admin.PostAsJsonAsync("/api/groups", new { name = "QA-" + Guid.NewGuid().ToString("N") })).Content.ReadFromJsonAsync<GroupDto>())!;
        (await admin.PostAsJsonAsync($"/api/groups/{group.Id}/members/changes", new { add = new[] { a } })).EnsureSuccessStatusCode();
        // another operator adds b after this editor loaded; the editor then adds c and removes a
        (await admin.PostAsJsonAsync($"/api/groups/{group.Id}/members/changes", new { add = new[] { b } })).EnsureSuccessStatusCode();
        var result = await (await admin.PostAsJsonAsync($"/api/groups/{group.Id}/members/changes", new { add = new[] { c, c }, remove = new[] { a } })).Content.ReadFromJsonAsync<GroupDto>();
        result!.MemberIds.Should().BeEquivalentTo(new[] { b, c });
        // idempotent re-add, non-admin refused, unknown workstation and contradictory input rejected
        (await admin.PostAsJsonAsync($"/api/groups/{group.Id}/members/changes", new { add = new[] { b } })).EnsureSuccessStatusCode();
        (await operatorClient.PostAsJsonAsync($"/api/groups/{group.Id}/members/changes", new { add = new[] { a } })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.PostAsJsonAsync($"/api/groups/{group.Id}/members/changes", new { add = new[] { Guid.NewGuid() } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync($"/api/groups/{group.Id}/members/changes", new { add = new[] { a }, remove = new[] { a } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync($"/api/groups/{group.Id}/members/changes", new { })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        factory.Query(db => db.GroupMembers.Count(m => m.GroupId == group.Id)).Should().Be(2);
    }
}
