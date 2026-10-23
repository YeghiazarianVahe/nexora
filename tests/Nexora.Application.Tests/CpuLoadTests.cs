using FluentAssertions;
using Nexora.Infrastructure.Wmi;

namespace Nexora.Application.Tests;

public sealed class CpuLoadTests
{
    [Fact]
    public void Total_utility_is_already_normalized_and_takes_precedence()
    {
        CpuLoad.Resolve(45, 20, [(10, 16)]).Should().Be(45);
        CpuLoad.Resolve(0, 20, [(10, 16)]).Should().Be(0);
        CpuLoad.Resolve(115, 20, []).Should().Be(100);
    }

    [Fact]
    public void Invalid_counters_fall_back_to_processor_time_then_weighted_socket_load()
    {
        CpuLoad.Resolve(-1, 32, []).Should().Be(32);
        CpuLoad.Resolve(double.NaN, 32, []).Should().Be(32);
        CpuLoad.Resolve(double.MaxValue, 32, []).Should().Be(32);
        CpuLoad.Resolve(null, -1, [(20, 4), (80, 12)]).Should().Be(65);
        CpuLoad.Resolve(null, double.PositiveInfinity, [(-1, 8), (40, 8)]).Should().Be(40);
        CpuLoad.Resolve(null, null, [(-1, 8)]).Should().BeNull();
    }

    [Fact]
    public void History_uses_a_real_sample_time_and_ignores_nonfinite_values()
    {
        var from = new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);
        var sample = from.AddSeconds(42);
        var buckets = Nexora.Application.Services.HistoryRange.Bucket([(sample, (double?)20), (sample.AddSeconds(1), double.NaN)], from, from.AddHours(1));
        buckets.Should().ContainSingle();
        buckets[0].Time.Should().Be(sample.AddSeconds(1));
        buckets[0].Value.Should().Be(20);
    }
}
