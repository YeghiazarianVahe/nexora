using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Api.Tests;

public sealed class PerformanceApiTests
{
    private sealed class Collector : IPerformanceCollector
    {
        public int Calls;
        public bool Fail;
        public Task<LiveSampleDto> CollectPerformanceAsync(string host, CancellationToken ct)
        {
            Calls++;
            return Fail ? Task.FromException<LiveSampleDto>(new IOException("SECRET\\remote-path"))
                : Task.FromResult(new LiveSampleDto(25, 16_000_000_000, 8_000_000_000, 8_000_000_000, 50, DateTime.UtcNow));
        }
    }
    private sealed class Factory : NexoraFactory
    {
        public readonly Collector Performance = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.AddSingleton<IPerformanceCollector>(Performance));
        }
    }
    private sealed record Reply(Guid EndpointId, string Hostname, LiveSampleDto Sample);

    [Fact]
    public async Task A_viewer_reads_fresh_cpu_and_ram_without_mutating_the_endpoint_or_writing_history()
    {
        using var factory = new Factory();
        var id = await factory.SeedEndpointAsync("LIVE-" + Guid.NewGuid().ToString("N"));
        var client = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        var before = factory.Query(db => db.MetricSamples.Count(m => m.EndpointId == id));
        var first = await client.GetAsync($"/api/endpoints/{id}/performance");
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        first.Headers.CacheControl!.NoStore.Should().BeTrue();
        var data = await first.Content.ReadFromJsonAsync<Reply>();
        data!.EndpointId.Should().Be(id);
        data.Sample.CpuLoadPercent.Should().Be(25);
        data.Sample.RamPercentUsed.Should().Be(50);
        await client.GetAsync($"/api/endpoints/{id}/performance");
        factory.Performance.Calls.Should().Be(1);
        factory.Query(db => db.MetricSamples.Count(m => m.EndpointId == id)).Should().Be(before);
        factory.Query(db => db.Endpoints.Single(e => e.Id == id).Online).Should().BeFalse();
    }

    [Fact]
    public async Task Anonymous_and_unknown_targets_do_not_start_a_read()
    {
        using var factory = new Factory();
        var anonymous = factory.ClientFor(null);
        (await anonymous.GetAsync($"/api/endpoints/{Guid.NewGuid()}/performance")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        (await viewer.GetAsync($"/api/endpoints/{Guid.NewGuid()}/performance")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        factory.Performance.Calls.Should().Be(0);
    }

    [Fact]
    public async Task An_unreachable_pc_returns_a_safe_retryable_response_and_backs_off()
    {
        using var factory = new Factory();
        factory.Performance.Fail = true;
        var id = await factory.SeedEndpointAsync("FAIL-LIVE-" + Guid.NewGuid().ToString("N"));
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        for (var i = 0; i < 2; i++)
        {
            var response = await viewer.GetAsync($"/api/endpoints/{id}/performance");
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await response.Content.ReadAsStringAsync()).Should().NotContain("SECRET");
        }
        factory.Performance.Calls.Should().Be(1);
    }
}
