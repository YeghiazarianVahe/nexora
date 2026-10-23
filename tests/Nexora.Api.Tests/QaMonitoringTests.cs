using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Api.Services;
using Nexora.Application;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Api.Tests;

public sealed class QaMonitoringTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    private sealed class Collector(Func<Task<EndpointSample>> sample) : IEndpointCollector
    {
        public Task<EndpointSample> CollectSampleAsync(string host, CancellationToken ct) => sample();
        public Task<EndpointDetailDto> CollectDetailAsync(string host, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<DiskRaw>> CollectDisksAsync(string host, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProfileInfo>> CollectProfilesAsync(string host, CancellationToken ct) => throw new NotSupportedException();
    }

    private async Task Poll(Guid id, string host, IEndpointCollector collector)
    {
        using var monitor = ActivatorUtilities.CreateInstance<MonitoringService>(factory.Services, collector);
        var poll = typeof(MonitoringService).GetMethod("PollOneAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)poll.Invoke(monitor, [id, host, new PlatformSettings(), CancellationToken.None])!;
    }

    [Fact]
    public async Task A_poll_of_the_old_address_is_discarded_after_readdressing()
    {
        var id = await factory.SeedEndpointAsync("OLD-PC");
        var host = factory.Query(db => db.Endpoints.Single(e => e.Id == id).Hostname);
        await Poll(id, host, new Collector(async () =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
            var row = db.Endpoints.Single(e => e.Id == id);
            row.Hostname = "NEW-" + Guid.NewGuid().ToString("N")[..8];
            row.LastDiskPollUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return await new FakeCollector().CollectSampleAsync(host, default);
        }));
        factory.Query(db => db.Endpoints.Single(e => e.Id == id).Online).Should().BeFalse();
        factory.Query(db => db.MetricSamples.Any(m => m.EndpointId == id)).Should().BeFalse();
    }

    [Fact]
    public async Task An_explicitly_empty_volume_result_replaces_old_capacity_data()
    {
        var id = await factory.SeedEndpointAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
            var row = db.Endpoints.Single(e => e.Id == id);
            row.VolumesJson = "[{\"Letter\":\"D:\",\"TotalBytes\":100,\"FreeBytes\":50}]";
            row.LastDiskPollUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        var host = factory.Query(db => db.Endpoints.Single(e => e.Id == id).Hostname);
        await Poll(id, host, new Collector(async () => (await new FakeCollector().CollectSampleAsync(host, default)) with { Volumes = [] }));
        factory.Query(db => db.Endpoints.Single(e => e.Id == id).VolumesJson).Should().Be("[]");
    }
}
