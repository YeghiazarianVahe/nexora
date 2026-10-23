using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Nexora.Api.Hubs;
using Nexora.Application;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Api.Tests;

/// <summary>H4: a poll cycle over many PCs costs one push a second and does not read the whole fleet per PC.</summary>
public sealed class PollScalingTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    private HubConnection Browser(string sid)
    {
        var raw = factory.CreateClient();
        raw.DefaultRequestHeaders.Add(TestAuthHandler.SidHeader, sid);
        raw.DefaultRequestHeaders.Add(TestAuthHandler.AccountHeader, "TEST-user");
        raw.DefaultRequestHeaders.Add(TestAuthHandler.GroupsHeader, TestIdentities.AccessGroupSid);
        var response = raw.GetAsync("/api/antiforgery").GetAwaiter().GetResult();
        var token = response.Content.ReadFromJsonAsync<TokenResponse>().GetAwaiter().GetResult()!.Token;
        var cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        return new HubConnectionBuilder().WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/nexora"), o =>
        {
            o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            o.Transports = HttpTransportType.LongPolling;
            o.Headers[TestAuthHandler.SidHeader] = sid;
            o.Headers[TestAuthHandler.AccountHeader] = "TEST-user";
            o.Headers[TestAuthHandler.GroupsHeader] = TestIdentities.AccessGroupSid;
            o.Headers["X-XSRF-TOKEN"] = token;
            o.Headers["Cookie"] = cookie;
        }).Build();
    }

    private static EndpointSummaryDto Dto(Guid id, double cpu) =>
        new(id, "pc", "pc", null, null, null, null, null, null, null, true, cpu, null, null, [], null, null, []);

    [Fact]
    public async Task Many_endpoint_updates_in_a_second_become_a_few_list_messages_carrying_the_latest_state()
    {
        var sid = TestIdentities.NewSid();
        await factory.SeedUserAsync(sid, Role.Viewer);
        var messages = new List<EndpointSummaryDto[]>();
        var browser = Browser(sid);
        browser.On<EndpointSummaryDto[]>("EndpointsUpdated", list => { lock (messages) messages.Add(list); });
        await browser.StartAsync();
        await Task.Delay(300);

        var notifier = (SignalRNotifier)factory.Services.GetRequiredService<INexoraNotifier>();
        var ids = Enumerable.Range(0, 450).Select(_ => Guid.NewGuid()).ToList();
        foreach (var id in ids) await notifier.EndpointUpdatedAsync(Dto(id, 10));
        foreach (var id in ids.Take(5)) await notifier.EndpointUpdatedAsync(Dto(id, 99)); // a newer state of the same PC
        await notifier.FlushAsync();

        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until) { lock (messages) if (messages.Sum(m => m.Length) >= 450) break; await Task.Delay(50); }
        List<EndpointSummaryDto[]> got; lock (messages) got = messages.ToList();

        got.Sum(m => m.Length).Should().Be(450, "one entry per endpoint, not one per update");
        got.Count.Should().BeLessThanOrEqualTo(3, "450 updates are three messages, not 455");
        got.SelectMany(m => m).Single(e => e.Id == ids[0]).CpuPercent.Should().Be(99, "the latest state wins");
        await browser.StopAsync();
    }

    [Fact]
    public async Task Building_one_endpoint_reads_only_its_own_group_and_drive_rows()
    {
        var a = await factory.SeedEndpointAsync("PC-SCALE-A");
        var b = await factory.SeedEndpointAsync("PC-SCALE-B");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
        db.DiskHealth.Add(new DiskHealthSnapshot { Id = Guid.NewGuid(), EndpointId = a, DiskKey = "A1", Model = "m", MediaType = "SSD", Status = "Warning", HealthPercent = 70 });
        db.DiskHealth.Add(new DiskHealthSnapshot { Id = Guid.NewGuid(), EndpointId = b, DiskKey = "B1", Model = "m", MediaType = "SSD", Status = "Critical", HealthPercent = 20 });
        await db.SaveChangesAsync();

        var endpoint = db.Endpoints.Single(e => e.Id == a);
        var dto = await scope.ServiceProvider.GetRequiredService<IEndpointDtoFactory>().BuildOneAsync(endpoint);

        dto.DiskHealth.Should().Be("Warning", "the other PC's Critical drive is not mixed in");
        dto.DiskHealthPercent.Should().Be(70);
    }

    [Fact]
    public async Task The_dashboard_counts_come_from_the_database_and_match_the_endpoints()
    {
        var sid = TestIdentities.NewSid();
        var viewer = await factory.UserClientAsync(sid, Role.Viewer);
        var before = (await viewer.GetFromJsonAsync<DashboardDto>("/api/dashboard"))!;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
            var hot = new ManagedEndpoint { Id = Guid.NewGuid(), Hostname = "PC-DASH-HOT", Online = true, CpuPercent = 99, RamPercent = 10, DiskPercent = 10, CreatedUtc = DateTime.UtcNow };
            var sick = new ManagedEndpoint { Id = Guid.NewGuid(), Hostname = "PC-DASH-SICK", Online = true, CpuPercent = 1, RamPercent = 1, DiskPercent = 1, CreatedUtc = DateTime.UtcNow };
            var off = new ManagedEndpoint { Id = Guid.NewGuid(), Hostname = "PC-DASH-OFF", Online = false, CpuPercent = 99, CreatedUtc = DateTime.UtcNow };
            db.Endpoints.AddRange(hot, sick, off);
            db.DiskHealth.Add(new DiskHealthSnapshot { Id = Guid.NewGuid(), EndpointId = sick.Id, DiskKey = "S", Model = "m", MediaType = "HDD", Status = "Critical", HealthPercent = 20 });
            await db.SaveChangesAsync();
        }

        var after = (await viewer.GetFromJsonAsync<DashboardDto>("/api/dashboard"))!;
        (after.Total - before.Total).Should().Be(3);
        (after.Online - before.Online).Should().Be(2);
        (after.Offline - before.Offline).Should().Be(1);
        (after.CpuAlerts - before.CpuAlerts).Should().Be(1, "an offline PC never alerts");
        (after.DiskHealthAlerts - before.DiskHealthAlerts).Should().Be(1);
        after.AlertEndpoints.Select(a => a.Hostname).Should().Contain(["PC-DASH-HOT", "PC-DASH-SICK"]).And.NotContain("PC-DASH-OFF");
        after.AlertEndpoints.Count.Should().BeLessThanOrEqualTo(50);
    }
}
