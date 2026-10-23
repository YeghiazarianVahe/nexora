using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Api.Tests;

public static class DeleteAll
{
    public sealed record Preview(int Count, string ConfirmToken);

    /// <summary>The two-step delete-all as the UI does it: preview (count + token), then remove with exactly that count and token.</summary>
    public static async Task<HttpResponseMessage> RunAsync(HttpClient admin)
    {
        var preview = (await (await admin.PostAsync("/api/endpoints/remove/preview", null)).Content.ReadFromJsonAsync<Preview>())!;
        return await admin.PostAsJsonAsync("/api/endpoints/remove", new { all = true, expectedCount = preview.Count, confirmToken = preview.ConfirmToken });
    }
}

/// <summary>H5 and M4: deletes are bounded, audited with a proof of what they covered, and delete-all is exactly what was confirmed.</summary>
public sealed class DeleteSafetyTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    [Fact]
    public async Task More_than_500_explicit_ids_in_one_call_are_refused_and_nothing_is_removed()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var keep = await factory.SeedEndpointAsync("PC-CAP");
        var ids = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).Append(keep).ToArray();

        var response = await admin.PostAsJsonAsync("/api/endpoints/remove", new { ids, all = false });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("500");
        factory.Query(db => db.Endpoints.Any(e => e.Id == keep)).Should().BeTrue();
    }

    [Fact]
    public async Task A_selected_delete_is_audited_with_its_mode_count_and_a_hash_of_the_sorted_ids()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var a = await factory.SeedEndpointAsync("PC-AUD-A");
        var b = await factory.SeedEndpointAsync("PC-AUD-B");

        (await admin.PostAsJsonAsync("/api/endpoints/remove", new { ids = new[] { b, a }, all = false })).StatusCode.Should().Be(HttpStatusCode.OK);

        var entry = factory.Query(db => db.AuditEntries.Where(x => x.Action == "endpoint.remove_many").OrderByDescending(x => x.Id).First());
        entry.Details.Should().Contain("\"mode\":\"selected\"").And.Contain("\"count\":2").And.Contain(EndpointService.IdsHash([a, b]));
        EndpointService.IdsHash([a, b]).Should().Be(EndpointService.IdsHash([b, a]), "the hash does not depend on the order");
    }

    [Fact]
    public async Task A_single_delete_is_audited_the_same_way_and_leaves_no_rows_behind()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var id = await factory.SeedEndpointAsync("PC-ONE");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Nexora.Application.INexoraDb>();
            db.DiskHealth.Add(new DiskHealthSnapshot { Id = Guid.NewGuid(), EndpointId = id, DiskKey = "D", Model = "m", MediaType = "SSD", Status = "OK" });
            db.MetricSamples.Add(new MetricSample { EndpointId = id, TimestampUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        (await admin.DeleteAsync($"/api/endpoints/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        factory.Query(db => db.Endpoints.Any(e => e.Id == id) || db.DiskHealth.Any(d => d.EndpointId == id) || db.MetricSamples.Any(m => m.EndpointId == id)).Should().BeFalse();
        var hash = EndpointService.IdsHash([id]);
        factory.Query(db => db.AuditEntries.Any(x => x.Action == "endpoint.remove" && x.Details!.Contains("\"mode\":\"single\"") && x.Details.Contains(hash))).Should().BeTrue();
    }

    [Fact]
    public async Task Delete_all_needs_a_preview_token_for_exactly_the_confirmed_count()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        await factory.SeedEndpointAsync("PC-ALL-1");

        (await admin.PostAsJsonAsync("/api/endpoints/remove", new { all = true })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync("/api/endpoints/remove", new { all = true, expectedCount = 1, confirmToken = "forged.token" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var preview = (await (await admin.PostAsync("/api/endpoints/remove/preview", null)).Content.ReadFromJsonAsync<DeleteAll.Preview>())!;
        (await admin.PostAsJsonAsync("/api/endpoints/remove", new { all = true, expectedCount = preview.Count + 1, confirmToken = preview.ConfirmToken })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        factory.Query(db => db.Endpoints.Count(e => !e.IsPinned)).Should().BeGreaterThan(0, "none of the refused calls removed anything");
    }

    [Fact]
    public async Task If_the_inventory_changes_after_the_preview_delete_all_removes_nothing()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        await factory.SeedEndpointAsync("PC-RACE-1");
        var preview = (await (await admin.PostAsync("/api/endpoints/remove/preview", null)).Content.ReadFromJsonAsync<DeleteAll.Preview>())!;
        var added = await factory.SeedEndpointAsync("PC-RACE-2"); // a workstation appears in between

        var response = await admin.PostAsJsonAsync("/api/endpoints/remove", new { all = true, expectedCount = preview.Count, confirmToken = preview.ConfirmToken });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        factory.Query(db => db.Endpoints.Any(e => e.Id == added)).Should().BeTrue();
    }

    [Fact]
    public async Task A_confirmed_delete_all_works_once_is_audited_as_mode_all_and_the_token_cannot_be_replayed()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        await factory.SeedEndpointAsync("PC-WIPE-1");
        var preview = (await (await admin.PostAsync("/api/endpoints/remove/preview", null)).Content.ReadFromJsonAsync<DeleteAll.Preview>())!;

        var first = await admin.PostAsJsonAsync("/api/endpoints/remove", new { all = true, expectedCount = preview.Count, confirmToken = preview.ConfirmToken });
        var replay = await admin.PostAsJsonAsync("/api/endpoints/remove", new { all = true, expectedCount = preview.Count, confirmToken = preview.ConfirmToken });

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        factory.Query(db => db.AuditEntries.Any(x => x.Action == "endpoint.remove_all" && x.Details!.Contains("\"mode\":\"all\""))).Should().BeTrue();
        factory.Query(db => db.Endpoints.Any(e => e.IsPinned)).Should().BeTrue();
    }
}
