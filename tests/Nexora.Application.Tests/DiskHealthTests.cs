using FluentAssertions;
using Nexora.Application.Services;

namespace Nexora.Application.Tests;

public sealed class DiskHealthEvaluatorTests
{
    private static DiskRaw Disk(
        string media = "HDD", string? health = "Healthy", bool predict = false, int? temp = 35, long? poh = 5000,
        int? wear = null, int? life = null, long? readErr = null, long? realloc = null, long? pending = null,
        long? uncorrectable = null, long? crc = null) =>
        new("K", "Model", "S", media, null, 1_000_000_000_000, health, predict, temp, null, poh, wear, life,
            readErr, null, realloc, pending, uncorrectable, crc, []);

    [Fact]
    public void Windows_status_alone_does_not_imply_100_percent_health()
    {
        var r = DiskHealthEvaluator.Evaluate(Disk());
        r.HealthPercent.Should().BeNull();
        r.Status.Should().Be("Unknown");
        r.Summary.Should().Contain("insufficient endurance or fault evidence");
    }

    [Fact]
    public void No_telemetry_is_unknown_not_healthy()
    {
        var r = DiskHealthEvaluator.Evaluate(Disk(health: "Unknown", temp: null, poh: null));
        r.Status.Should().Be("Unknown");
        r.HealthPercent.Should().BeNull();
    }

    [Theory]
    [InlineData(1, 90, "OK")]        // a single reallocated sector is noted, not alarming
    [InlineData(10, 80, "Warning")]
    [InlineData(100, 65, "Warning")]
    [InlineData(500, 55, "Warning")]
    public void Reallocated_sectors_lower_the_score_in_steps(long count, int expected, string status)
    {
        var r = DiskHealthEvaluator.Evaluate(Disk(realloc: count));
        r.HealthPercent.Should().Be(expected);
        r.Status.Should().Be(status);
        r.Summary.Should().Contain("Reallocated");
    }

    [Fact]
    public void Pending_sectors_are_a_serious_sign()
    {
        var r = DiskHealthEvaluator.Evaluate(Disk(pending: 5));
        r.HealthPercent.Should().Be(75);
        r.Status.Should().Be("Warning");
    }

    [Fact]
    public void Predicted_failure_is_always_critical()
    {
        var r = DiskHealthEvaluator.Evaluate(Disk(predict: true));
        r.Status.Should().Be("Critical");
        r.HealthPercent.Should().BeLessThanOrEqualTo(10);
        r.Summary.Should().Contain("imminent failure");
    }

    [Fact]
    public void Windows_unhealthy_status_is_critical()
    {
        var r = DiskHealthEvaluator.Evaluate(Disk(health: "Unhealthy"));
        r.Status.Should().Be("Critical");
    }

    [Theory]
    [InlineData("HDD", 49, "OK")]
    [InlineData("HDD", 52, "Warning")]
    [InlineData("HDD", 62, "Critical")]
    [InlineData("SSD", 59, "OK")]
    [InlineData("NVMe", 68, "OK")]        // v4: NVMe defaults are 70 / 80
    [InlineData("NVMe", 72, "Warning")]
    [InlineData("NVMe", 82, "Critical")]
    public void Temperature_is_judged_on_its_own_and_never_lowers_health(string media, int temp, string tempStatus)
    {
        var r = DiskHealthEvaluator.Evaluate(Disk(media: media, temp: temp));
        r.TempStatus.Should().Be(tempStatus);
        r.TempC.Should().Be(temp);
        r.HealthPercent.Should().BeNull();
        r.Status.Should().Be("Unknown");
    }

    [Fact]
    public void Ssd_remaining_life_caps_the_score()
    {
        DiskHealthEvaluator.Evaluate(Disk("NVMe", wear: 20)).HealthPercent.Should().Be(80);
        DiskHealthEvaluator.Evaluate(Disk("SSD", life: 30)).Status.Should().Be("Critical");
    }

    [Fact]
    public void Remaining_days_are_estimated_from_the_wear_rate()
    {
        // 10% worn in 100 days -> 90% left -> 900 days.
        var r = DiskHealthEvaluator.Evaluate(Disk("SSD", poh: 2400, wear: 10));
        r.EstimatedDaysLeft.Should().Be(900);
        DiskHealthEvaluator.Evaluate(Disk("HDD", poh: 2400)).EstimatedDaysLeft.Should().BeNull();
    }

    [Fact]
    public void Hours_of_use_alone_do_not_lower_a_hard_drives_score()
    {
        DiskHealthEvaluator.Evaluate(Disk(poh: 70_000)).HealthPercent.Should().BeNull("hours alone cannot establish health");
    }
}

public sealed class SmartParserTests
{
    private static byte[] Block(params (byte id, byte current, byte worst, long raw)[] attrs)
    {
        var b = new byte[512];
        for (var i = 0; i < attrs.Length; i++)
        {
            var o = 2 + i * 12;
            b[o] = attrs[i].id;
            b[o + 3] = attrs[i].current;
            b[o + 4] = attrs[i].worst;
            for (var k = 0; k < 6; k++) b[o + 5 + k] = (byte)((attrs[i].raw >> (8 * k)) & 0xFF);
        }
        return b;
    }

    [Fact]
    public void Decodes_attributes_with_48_bit_raw_values()
    {
        var attrs = SmartParser.Parse(Block((5, 100, 100, 0), (9, 90, 90, 7806), (194, 62, 40, 0x0000_0001_0026)));

        attrs.Should().HaveCount(3);
        SmartParser.Raw(attrs, 9).Should().Be(7806);
        attrs.Single(a => a.Id == 5).Name.Should().Be("Reallocated sectors");
        SmartParser.Temperature(attrs).Should().Be(38); // only the low byte of the raw value is the temperature
    }

    [Fact]
    public void Life_left_comes_from_the_first_life_attribute_present()
    {
        SmartParser.LifeLeft(SmartParser.Parse(Block((231, 87, 87, 0), (177, 50, 50, 0)))).Should().Be(87);
        SmartParser.LifeLeft(SmartParser.Parse(Block((9, 90, 90, 100)))).Should().BeNull();
    }

    [Fact]
    public void Garbage_and_short_blocks_do_not_throw()
    {
        SmartParser.Parse(null).Should().BeEmpty();
        SmartParser.Parse(new byte[5]).Should().BeEmpty();
        SmartParser.Parse(new byte[512]).Should().BeEmpty();
    }
}

public sealed class HistoryRangeTests
{
    [Theory]
    [InlineData("1h", 1)]
    [InlineData("6h", 6)]
    [InlineData("24h", 24)]
    [InlineData("nonsense", 24)]
    public void Ranges_parse_with_a_safe_default(string range, int hours) =>
        HistoryRange.Parse(range).Should().Be(TimeSpan.FromHours(hours));

    [Fact]
    public void Bucketing_caps_the_point_count_and_averages()
    {
        var from = new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddDays(1);
        var rows = Enumerable.Range(0, 1440).Select(i => (from.AddMinutes(i), (double?)(i % 2 == 0 ? 10 : 20))).ToList();

        var buckets = HistoryRange.Bucket(rows, from, to, 240);

        buckets.Count.Should().BeLessThanOrEqualTo(240);
        buckets.Should().OnlyContain(b => b.Value == 15);
    }

    [Fact]
    public void Missing_values_stay_gaps()
    {
        var from = DateTime.UtcNow.AddHours(-1);
        var buckets = HistoryRange.Bucket([(from.AddMinutes(1), (double?)null)], from, from.AddHours(1));
        buckets.Should().ContainSingle().Which.Value.Should().BeNull();
    }
}
