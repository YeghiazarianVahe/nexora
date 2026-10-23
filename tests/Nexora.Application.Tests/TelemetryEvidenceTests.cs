using System.Text.Json;
using FluentAssertions;
using Nexora.Infrastructure.Wmi;

namespace Nexora.Application.Tests;
public sealed class TelemetryEvidenceTests
{
    [Theory]
    [InlineData(0,false)] [InlineData(3,true)] [InlineData(11,true)] [InlineData(300,true)]
    public void Collector_interruption_keeps_the_original_time_and_marks_last_known_evidence(int ageMinutes,bool stale)
    {
        var now=DateTime.UtcNow;
        var json=JsonSerializer.Serialize(new {Machine="PC",CollectedUtc=now.AddMinutes(-ageMinutes),Disks=new[]{new RemoteNvmeReader.NativeDisk(0,"HDD","serial","","", "AA==")}});
        var disk=RemoteNvmeReader.ParseLocalCache(json,"PC",now).Single();
        disk.SourceSampledUtc.Should().Be(now.AddMinutes(-ageMinutes)); disk.SourceStale.Should().Be(stale);
        if(stale) disk.SourceNote.Should().NotBeNullOrWhiteSpace();
    }
    [Fact]
    public void Foreign_machine_future_and_missing_sample_times_are_rejected()
    {
        var now=DateTime.UtcNow;
        foreach(var json in new[]{JsonSerializer.Serialize(new{Machine="OTHER",CollectedUtc=now,Disks=Array.Empty<object>()}),JsonSerializer.Serialize(new{Machine="PC",CollectedUtc=now.AddMinutes(5),Disks=Array.Empty<object>()}),"{\"Machine\":\"PC\",\"Disks\":[]}"})
            RemoteNvmeReader.ParseLocalCache(json,"PC",now).Should().BeEmpty();
    }
    [Fact]
    public void Wmi_native_duplicate_and_unmatched_records_are_accounted_for_by_consumed_identity()
    {
        SmartEvidenceAccounting.Count(["DISK-A_0"],[]).Should().Be(1);
        SmartEvidenceAccounting.Count([], ["0"]).Should().Be(1);
        // Both available: WMI was consumed, so native is not counted a second time.
        SmartEvidenceAccounting.Count(["DISK-A_0","disk-a_1"],[]).Should().Be(1);
        SmartEvidenceAccounting.Count(["DISK-A_0"],["0"],["DISK-A_0"]).Should().Be(1);
        SmartEvidenceAccounting.Count(["DISK-A_0","UNMATCHED_0"],["1"]).Should().Be(3);
        SmartEvidenceAccounting.Count([], ["0","1"]).Should().Be(2,"distinct physical indexes must stay distinct even for duplicate serials");
    }
    [Fact]
    public void Conflicting_provider_data_with_the_same_name_is_not_silently_merged()
    {
        SmartEvidenceAccounting.CountProviders(new[]{new SmartEvidenceAccounting.ProviderRecord("DISK-A_0","one"),new SmartEvidenceAccounting.ProviderRecord("DISK-A_1","two")},Array.Empty<string>()).Should().Be(2);
    }
}
