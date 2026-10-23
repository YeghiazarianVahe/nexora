using System.Net.Http.Json;
using FluentAssertions;
using Nexora.Application;
using Nexora.Domain;

namespace Nexora.Api.Tests;

/// <summary>N5: the dashboard card, the "Needs attention" filter and the export filter share ONE definition of a drive-health alert.</summary>
public sealed class DriveAlertDefinitionTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    private sealed record Preview(int Count);

    private async Task SeedAsync(string tag, bool online, string status)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
        var e = new ManagedEndpoint { Id = Guid.NewGuid(), Hostname = tag, Online = online, OsCaption = "Windows 11", CpuPercent = 1, RamPercent = 1, DiskPercent = 1, CreatedUtc = DateTime.UtcNow };
        db.Endpoints.Add(e);
        db.DiskHealth.Add(new DiskHealthSnapshot { Id = Guid.NewGuid(), EndpointId = e.Id, DiskKey = tag, Model = "m", MediaType = "SSD", Status = status, HealthPercent = status == "OK" ? 95 : 40 });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task An_offline_PC_with_a_bad_drive_is_not_an_alert_anywhere()
    {
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        var tag = "dah-" + Guid.NewGuid().ToString("N")[..6];
        var before = (await viewer.GetFromJsonAsync<DashboardDto>("/api/dashboard"))!.DiskHealthAlerts;
        await SeedAsync(tag + "-on", online: true, "Critical");
        await SeedAsync(tag + "-off", online: false, "Critical");
        await SeedAsync(tag + "-ok", online: true, "OK");

        var card = (await viewer.GetFromJsonAsync<DashboardDto>("/api/dashboard"))!.DiskHealthAlerts - before;
        var filtered = await (await viewer.PostAsJsonAsync("/api/endpoints/export?preview=true", new { filter = new { search = tag, health = "attention" } })).Content.ReadFromJsonAsync<Preview>();
        var list = (await viewer.GetFromJsonAsync<List<EndpointSummaryDto>>("/api/endpoints"))!.Where(e => e.Hostname.StartsWith(tag) && e.Alerts.Contains("diskhealth")).ToList();

        card.Should().Be(1, "only the online PC raises the alert");
        filtered!.Count.Should().Be(1, "the export filter agrees with the card");
        list.Should().ContainSingle().Which.Hostname.Should().Be(tag + "-on");
    }
}
