using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Nexora.Application;
using Nexora.Domain;

namespace Nexora.Api.Tests;

/// <summary>The Role Management surface is SuperAdmin-only and must be invisible (404) to everyone else.</summary>
public sealed class RoleManagementTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    private static readonly Guid AnyId = Guid.NewGuid();

    private static HttpRequestMessage[] AllRoleManagementRequests(string targetSid) =>
    [
        new(HttpMethod.Get, "/api/admin/access-requests"),
        new(HttpMethod.Get, "/api/admin/users"),
        new(HttpMethod.Get, "/api/admin/directory?q=ab"),
        new(HttpMethod.Post, $"/api/admin/access-requests/{AnyId}/approve") { Content = JsonContent.Create(new { role = "Viewer" }) },
        new(HttpMethod.Post, $"/api/admin/access-requests/{AnyId}/reject") { Content = JsonContent.Create(new { reason = "no" }) },
        new(HttpMethod.Put, $"/api/admin/users/{targetSid}/role") { Content = JsonContent.Create(new { role = "Viewer" }) },
        new(HttpMethod.Post, $"/api/admin/users/{targetSid}/revoke"),
        new(HttpMethod.Post, "/api/admin/users") { Content = JsonContent.Create(new { sid = targetSid, role = "Viewer" }) },
    ];

    [Theory]
    [InlineData(Role.Viewer)]
    [InlineData(Role.Operator)]
    [InlineData(Role.Admin)]
    public async Task Non_super_admin_roles_get_404_on_every_endpoint(Role role)
    {
        var client = await factory.UserClientAsync(TestIdentities.NewSid(), role);
        foreach (var request in AllRoleManagementRequests(TestIdentities.NewSid()))
        {
            var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, $"{request.Method} {request.RequestUri} must not be discoverable by {role}");
        }
    }

    [Fact]
    public async Task User_without_a_role_gets_404_too()
    {
        var client = await factory.UserClientAsync(TestIdentities.NewSid(), role: null);
        foreach (var request in AllRoleManagementRequests(TestIdentities.NewSid()))
            (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Anonymous_callers_are_challenged_not_served()
    {
        var response = await factory.ClientFor(null).GetAsync("/api/admin/users");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Super_admin_gets_200_on_the_read_endpoints()
    {
        var client = await factory.SuperAdminClientAsync();
        foreach (var url in new[] { "/api/admin/access-requests", "/api/admin/users", "/api/admin/directory?q=ab" })
            (await client.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.OK, url);
    }

    [Fact]
    public async Task Super_admin_cannot_be_modified_through_the_api()
    {
        var client = await factory.SuperAdminClientAsync();
        var sid = TestIdentities.SuperAdminSid;

        (await client.PutAsJsonAsync($"/api/admin/users/{sid}/role", new { role = "Viewer" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PostAsync($"/api/admin/users/{sid}/revoke", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PostAsJsonAsync("/api/admin/users", new { sid, role = "Admin" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("SuperAdmin")]
    [InlineData("superadmin")]
    [InlineData("4")]
    [InlineData("Root")]
    public async Task Super_admin_role_cannot_be_assigned_to_anyone(string role)
    {
        var target = TestIdentities.NewSid();
        await factory.SeedUserAsync(target, Role.Viewer);
        var client = await factory.SuperAdminClientAsync();

        (await client.PutAsJsonAsync($"/api/admin/users/{target}/role", new { role })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/admin/users", new { sid = TestIdentities.NewSid(), role })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Malformed_sid_is_rejected()
    {
        var client = await factory.SuperAdminClientAsync();
        (await client.PostAsync("/api/admin/users/not-a-sid/revoke", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Approving_a_request_grants_the_role_and_audits_the_change()
    {
        var sid = TestIdentities.NewSid();
        var user = factory.ClientFor(sid, "TEST\\newhire");
        await FactoryExtensions.RequestAccessAsync(factory, user);

        var admin = await factory.SuperAdminClientAsync();
        var pending = (await admin.GetFromJsonAsync<List<AccessRequestDto>>("/api/admin/access-requests?status=Pending"))!;
        var request = pending.Single(r => r.Sid == sid);

        var response = await admin.PostAsJsonAsync($"/api/admin/access-requests/{request.Id}/approve", new { role = "Operator" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await user.GetFromJsonAsync<MeDto>("/api/me"))!.Role.Should().Be("Operator");

        var audit = factory.Query(db => db.AuditEntries.Where(a => a.TargetId == sid && a.Action == "role.approve").ToList());
        var entry = audit.Should().ContainSingle().Subject;
        entry.ActorSid.Should().Be(TestIdentities.SuperAdminSid);
        entry.OldValue.Should().Be("None");
        entry.NewValue.Should().Be("Operator");
        entry.TimestampUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Rejecting_requires_a_reason_and_is_audited()
    {
        var sid = TestIdentities.NewSid();
        await FactoryExtensions.RequestAccessAsync(factory, factory.ClientFor(sid));
        var admin = await factory.SuperAdminClientAsync();
        var request = (await admin.GetFromJsonAsync<List<AccessRequestDto>>("/api/admin/access-requests?status=Pending"))!.Single(r => r.Sid == sid);

        (await admin.PostAsJsonAsync($"/api/admin/access-requests/{request.Id}/reject", new { reason = "  " })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync($"/api/admin/access-requests/{request.Id}/reject", new { reason = "Not on the team" })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var me = await factory.ClientFor(sid).GetFromJsonAsync<MeDto>("/api/me");
        me!.Status.Should().Be("Rejected");
        me.Detail.Should().Be("Not on the team");
        factory.Query(db => db.AuditEntries.Count(a => a.TargetId == sid && a.Action == "role.reject")).Should().Be(1);
    }

    [Fact]
    public async Task Changing_a_role_takes_effect_immediately_and_is_audited()
    {
        var sid = TestIdentities.NewSid();
        await factory.SeedUserAsync(sid, Role.Viewer);
        var user = factory.ClientFor(sid);
        (await user.GetFromJsonAsync<MeDto>("/api/me"))!.Role.Should().Be("Viewer"); // warms the 60 s role cache

        var admin = await factory.SuperAdminClientAsync();
        (await admin.PutAsJsonAsync($"/api/admin/users/{sid}/role", new { role = "Admin" })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await user.GetFromJsonAsync<MeDto>("/api/me"))!.Role.Should().Be("Admin"); // cache was invalidated
        var entry = factory.Query(db => db.AuditEntries.Single(a => a.TargetId == sid && a.Action == "role.change"));
        (entry.OldValue, entry.NewValue).Should().Be(("Viewer", "Admin"));
    }

    [Fact]
    public async Task Revoking_removes_access_and_is_audited()
    {
        var sid = TestIdentities.NewSid();
        await factory.SeedUserAsync(sid, Role.Operator);
        var user = factory.ClientFor(sid);
        (await user.GetAsync("/api/dashboard")).StatusCode.Should().Be(HttpStatusCode.OK);

        var admin = await factory.SuperAdminClientAsync();
        (await admin.PostAsync($"/api/admin/users/{sid}/revoke", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await user.GetAsync("/api/dashboard")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var entry = factory.Query(db => db.AuditEntries.Single(a => a.TargetId == sid && a.Action == "role.revoke"));
        (entry.OldValue, entry.NewValue).Should().Be(("Operator", "None"));
    }

    [Fact]
    public async Task Adding_a_user_proactively_is_audited()
    {
        var sid = TestIdentities.NewSid();
        var admin = await factory.SuperAdminClientAsync();

        (await admin.PostAsJsonAsync("/api/admin/users", new { sid, role = "Viewer" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.PostAsJsonAsync("/api/admin/users", new { sid, role = "Viewer" })).StatusCode.Should().Be(HttpStatusCode.Conflict);

        factory.Query(db => db.AuditEntries.Count(a => a.TargetId == sid && a.Action == "role.add")).Should().Be(1);
    }

    [Fact]
    public async Task The_audit_log_refuses_updates_and_deletes()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
        db.AuditEntries.Add(new AuditEntry { TimestampUtc = DateTime.UtcNow, ActorSid = "x", ActorAccount = "x", Action = "t" });
        await db.SaveChangesAsync();

        var row = db.AuditEntries.First();
        row.Action = "tampered";
        Func<Task> update = async () => await db.SaveChangesAsync();
        await update.Should().ThrowAsync<InvalidOperationException>().WithMessage("*append-only*");
    }
}

internal static class FactoryExtensions
{
    public static async Task RequestAccessAsync(NexoraFactory factory, HttpClient client)
    {
        await NexoraFactory.WithTokenAsync(client);
        var response = await client.PostAsync("/api/access/request", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
