using FluentAssertions;
using Nexora.Application.Services;

namespace Nexora.Application.Tests;

public sealed class HddPublishedMethodTests
{
    private static DiskRaw Drive(params SmartAttribute[] attributes) => new("hdd", "HDD", "serial", "HDD", "SATA", 1000, "Healthy", false,
        35, null, 10000, null, null, null, null, SmartParser.Raw(attributes, 5), SmartParser.Raw(attributes, 197), SmartParser.Raw(attributes, 198), null, attributes);
    private static DiskHealthResult Evaluate(params SmartAttribute[] attributes) => DiskHealthEvaluator.Evaluate(Drive(attributes), new HealthWeights { HddMethod = "smart-raw" });
    [Fact]
    public void Multiple_faults_and_conflicting_sector_fields_keep_conservative_severity_and_a_balanced_ledger()
    {
        var drive=Drive(new SmartAttribute(5,"Reallocated",100,100,2),new SmartAttribute(197,"Pending",100,100,6),new SmartAttribute(198,"Offline",100,100,1)) with {Pending=0,ReadErrors=50};
        var result=DiskHealthEvaluator.Evaluate(drive,new HealthWeights{HddMethod="smart-raw"});
        result.Status.Should().Be(DiskStatus.Critical);
        result.HealthPercent.Should().BeLessThan(100);
        result.Reasons!.Sum(r=>r.Delta).Should().Be(result.HealthPercent-100);
        result.Reasons.Should().Contain(r=>r.Code=="io");
    }
    [Fact]
    public void Previously_failed_drive_does_not_become_healthy_when_fault_counts_are_zero()
    {
        var drive=Drive(new SmartAttribute(5,"Reallocated",100,100,0)) with {PredictFailure=true};
        var result=DiskHealthEvaluator.Evaluate(drive,new HealthWeights{HddMethod="smart-raw"});
        result.Status.Should().Be(DiskStatus.Critical);result.HealthPercent.Should().Be(0);
    }
    [Fact]
    public void Uncorrected_errors_are_visible_without_counting_the_same_reported_fault_twice()
    {
        var drive = Drive(new SmartAttribute(5,"Reallocated",100,100,0), new SmartAttribute(187,"Reported uncorrectable",100,100,50)) with { ReadErrors = 50 };
        var result = DiskHealthEvaluator.Evaluate(drive, new HealthWeights { HddMethod = "smart-raw" });
        result.Status.Should().Be(DiskStatus.Warning);
        result.Reasons.Should().Contain(r => r.Code == "io");
        result.Reasons.Should().NotContain(r => r.Code == "187");
    }
    [Theory]
    [InlineData(0, 50)] [InlineData(50, 0)]
    public void Explicit_fault_evidence_never_disappears_from_published_scoring(long io, long reported)
    {
        var drive = Drive(new SmartAttribute(5,"Reallocated",100,100,0), new SmartAttribute(187,"Reported uncorrectable",100,100,reported)) with { ReadErrors = io };
        var result = DiskHealthEvaluator.Evaluate(drive, new HealthWeights { HddMethod = "smart-raw" });
        result.Status.Should().NotBe(DiskStatus.Ok);
        result.Reasons.Should().Contain(r => r.Code == (io > 0 ? "io" : "187"));
    }
    [Fact]
    public void Published_percentage_and_existing_critical_sector_policy_are_independent()
    {
        var drive = Drive(new SmartAttribute(5,"Reallocated",100,100,0),new SmartAttribute(197,"Pending",100,100,6));
        var conservative = DiskHealthEvaluator.Evaluate(drive,new HealthWeights { HddMethod="smart-raw" });
        conservative.HealthPercent.Should().Be(96);
        conservative.Status.Should().Be(DiskStatus.Critical);
        DiskHealthEvaluator.Evaluate(drive,new HealthWeights { HddMethod="smart-raw", HddCriticalSectorStatus=false }).Status.Should().Be(DiskStatus.Warning);
    }
    [Fact]
    public void Published_example_multiplies_reallocated_and_pending_degradation()
    {
        var result = Evaluate(new SmartAttribute(5,"Reallocated",100,100,2),new SmartAttribute(197,"Pending",100,100,5));
        result.HealthPercent.Should().Be(95);
        result.Reasons!.Sum(r => r.Delta).Should().Be(-5);
    }
    [Fact]
    public void Per_attribute_degradation_is_capped()
    {
        Evaluate(new SmartAttribute(198,"Uncorrectable",100,100,512)).HealthPercent.Should().Be(30);
    }
    [Fact]
    public void Packed_raw_fields_use_only_the_low_word()
    {
        Evaluate(new SmartAttribute(5,"Reallocated",100,100,0x10002)).HealthPercent.Should().Be(98);
    }
    [Fact]
    public void Clean_smart_fault_counters_provide_100_but_absent_evidence_does_not()
    {
        Evaluate(new SmartAttribute(5,"Reallocated",100,100,0)).HealthPercent.Should().Be(100);
        Evaluate().HealthPercent.Should().BeNull();
    }
    [Fact]
    public void Seagate_seek_operation_counter_is_not_treated_as_failed_seeks()
    {
        var drive = Drive(new SmartAttribute(5,"Reallocated",100,100,0),new SmartAttribute(7,"Seek rate",71,60,13134871,45)) with { Model = "ST1000VX012-3CU10C" };
        DiskHealthEvaluator.Evaluate(drive,new HealthWeights { HddMethod = "smart-raw" }).HealthPercent.Should().Be(100);
    }
    [Fact]
    public void Failed_threshold_is_critical_and_zero_health()
    {
        var result = Evaluate(new SmartAttribute(5,"Reallocated",1,1,5,10));
        result.Status.Should().Be(DiskStatus.Critical);
        result.HealthPercent.Should().Be(0);
        result.Reasons!.Sum(r => r.Delta).Should().Be(-100);
    }
}

