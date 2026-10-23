using System.Net.Http.Json;
using FluentAssertions;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Api.Tests;

public sealed class StorageEvidenceApiTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    [Fact]
    public async Task Cached_source_time_is_not_relabelled_and_recovery_restores_current_measurements()
    {
        var id=await factory.SeedEndpointAsync("CACHED-SOURCE",online:true);
        using var scope=factory.Services.CreateScope();
        var service=scope.ServiceProvider.GetRequiredService<IDiskHealthService>();
        var sampled=DateTime.UtcNow.AddMinutes(-12);
        var raw=new DiskRaw("S","Cached HDD","S","HDD","SATA",1000,"Healthy",false,43,null,100,null,null,null,null,0,0,0,0,
            [new SmartAttribute(5,"Reallocated",100,100,0)],SourceSampledUtc:sampled,SourceStale:true,SourceNote:"Collector interrupted");
        await service.StoreAsync(id,[raw]);
        var stale=(await service.GetAsync(id,null)).Single();
        stale.UpdatedUtc.Should().BeCloseTo(sampled,TimeSpan.FromMilliseconds(1));
        stale.ScanCompletedUtc.Should().BeAfter(sampled);
        stale.Stale.Should().BeTrue(); stale.TemperatureC.Should().BeNull(); stale.LastKnownTemperatureC.Should().Be(43);
        // A corrupt/unreadable cache must not erase useful last-known health.
        await service.StoreAsync(id,[raw with { Attributes=[],Reallocated=null,Pending=null,OfflineUncorrectable=null,SourceSampledUtc=null }]);
        (await service.GetAsync(id,null)).Single().HealthPercent.Should().Be(stale.HealthPercent);
        await service.StoreAsync(id,[raw with { SourceSampledUtc=DateTime.UtcNow,SourceStale=false,SourceNote=null,TemperatureC=41 }]);
        var recovered=(await service.GetAsync(id,null)).Single();
        recovered.Stale.Should().BeFalse(); recovered.TemperatureC.Should().Be(41); recovered.SourceNote.Should().BeNull();
    }
    [Fact]
    public async Task An_invalid_new_temperature_keeps_the_raw_last_good_reading_separate()
    {
        var id = await factory.SeedEndpointAsync("FIXTURE-LAST-GOOD", online: true);
        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IDiskHealthService>();
            var raw = new DiskRaw("SSD", "Fixture SSD", "S", "SSD", "SATA", 512_000_000_000,
                "Healthy", false, 43, null, 1000, 4, 96, null, null, null, null, null, null, []);
            await service.StoreAsync(id, [raw]);
            await service.StoreAsync(id, [raw with { TemperatureC = 255 }]);
        }
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        var result = (await viewer.GetFromJsonAsync<List<DiskHealthDto>>($"/api/endpoints/{id}/disks"))!.Single();
        result.TemperatureC.Should().BeNull();
        result.LastKnownTemperatureC.Should().Be(43);
        result.Stale.Should().BeFalse("the scan is current, but its temperature is unavailable");
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Stored_scans_expose_confidence_and_suppress_stale_current_temperature(bool stale)
    {
        var id = await factory.SeedEndpointAsync("FIXTURE-STORAGE", online: true);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Nexora.Infrastructure.Persistence.NexoraDbContext>();
            db.DiskHealth.Add(new DiskHealthSnapshot { Id = Guid.NewGuid(), EndpointId = id, DiskKey = "NVME",
                Model = "Fixture SSD", MediaType = "NVMe", BusType = "NVMe", HealthPercent = 96, Status = "OK", TemperatureC = 42, ScoreVersion = DiskHealthEvaluator.ScoreVersion,
                ReasonsJson = """[{"Code":"nvme-source","Text":"Standard log","Delta":0}]""",
                UpdatedUtc = DateTime.UtcNow.AddMinutes(stale ? -45 : -1) });
            await db.SaveChangesAsync();
        }
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        var result = (await viewer.GetFromJsonAsync<List<DiskHealthDto>>($"/api/endpoints/{id}/disks"))!.Single();
        result.Stale.Should().Be(stale);
        result.Confidence.Should().Be(stale ? "Low" : "High");
        result.ValueKind.Should().Be("estimated");
        result.TemperatureC.Should().Be(stale ? null : 42);
        result.LastKnownTemperatureC.Should().Be(42);
    }
}
