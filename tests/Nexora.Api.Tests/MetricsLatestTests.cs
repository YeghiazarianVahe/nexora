using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Api.Tests;

public sealed class MetricsLatestTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    [Fact]
    public async Task The_final_chart_point_is_the_latest_real_sample_rather_than_an_interval_average()
    {
        var id = await factory.SeedEndpointAsync();
        var latest = DateTime.UtcNow.AddSeconds(-2);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
            db.MetricSamples.AddRange(
                new MetricSample { EndpointId = id, TimestampUtc = latest.AddSeconds(-8), Cpu = 10, Ram = 40, Disk = 30 },
                new MetricSample { EndpointId = id, TimestampUtc = latest, Cpu = 80, Ram = 60, Disk = 35 },
                new MetricSample { EndpointId = id, TimestampUtc = latest.AddDays(1), Cpu = 99 });
            await db.SaveChangesAsync();
        }
        var client = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        foreach (var range in new[] { "1h", "6h", "7d" })
        {
            var points = await client.GetFromJsonAsync<List<MetricPoint>>($"/api/endpoints/{id}/metrics?range={range}");
            points.Should().NotBeEmpty();
            points![^1].Cpu.Should().Be(80);
            points[^1].Ram.Should().Be(60);
            points[^1].Disk.Should().Be(35);
            points[^1].TimeUtc.Should().Be(latest);
        }
    }
}
