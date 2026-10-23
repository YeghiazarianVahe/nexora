using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Nexora.Application.Services;

namespace Nexora.Application.Tests;

/// <summary>
/// The fleet runs mixed locales. These fixtures run the pure WMI value rules under hy-AM and ru-RU with explicitly forced separators
/// (decimal comma, space/NBSP digit grouping), so the result is the same even where the OS has no culture data (invariant-globalization builds).
/// </summary>
public sealed class InventoryCultureTests
{
    public static IEnumerable<object[]> Cultures() => [["hy-AM"], ["ru-RU"], ["invariant"]];

    private static CultureInfo Hostile(string name)
    {
        if (name == "invariant") return CultureInfo.InvariantCulture;
        var c = (CultureInfo)CultureInfo.GetCultureInfo(name).Clone();
        c.NumberFormat.NumberDecimalSeparator = ",";
        c.NumberFormat.NumberGroupSeparator = " ";
        c.NumberFormat.NegativeSign = "−";
        c.DateTimeFormat.DateSeparator = ".";
        c.DateTimeFormat.TimeSeparator = "-";
        return c;
    }

    private static T Under<T>(string culture, Func<T> action)
    {
        var (old, oldUi) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = Hostile(culture);
            return action();
        }
        finally { (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (old, oldUi); }
    }

    [Theory, MemberData(nameof(Cultures))]
    public void CIM_datetime_strings_parse_the_same_in_every_culture(string culture)
    {
        // 2026-10-01 08:30:15.123456 at UTC+4 (Armenia, 240 minutes) is 04:30:15 UTC
        var parsed = Under(culture, () => HardwareMapping.ParseCimDateTime("20261001083015.123456+240"));
        parsed.Should().Be(new DateTime(2026, 10, 1, 4, 30, 15, DateTimeKind.Utc).AddTicks(1_234_560));
        parsed!.Value.Kind.Should().Be(DateTimeKind.Utc);
        Under(culture, () => HardwareMapping.ParseCimDateTime("20261001083015.000000-060"))
            .Should().Be(new DateTime(2026, 10, 1, 9, 30, 15, DateTimeKind.Utc));
    }

    [Theory, MemberData(nameof(Cultures))]
    public void Malformed_and_zero_CIM_dates_are_null(string culture)
    {
        foreach (var bad in new[] { null, "", "garbage", "16010101000000.000000+000", "20261301083015.123456+240", "2026100108301.123456+240", "20261001083015.123456*240" })
            Under(culture, () => HardwareMapping.ParseCimDateTime(bad)).Should().BeNull(bad);
    }

    [Theory, MemberData(nameof(Cultures))]
    public void Localized_OS_text_is_kept_verbatim_and_build_numbers_never_use_culture_separators(string culture)
    {
        Under(culture, () => HardwareMapping.Clean("  Майкрософт Windows 11 Профессиональная  ")).Should().Be("Майкрософт Windows 11 Профессиональная");
        Under(culture, () => HardwareMapping.Clean("Microsoft Windows 11 Տնային")).Should().Be("Microsoft Windows 11 Տնային");
        Under(culture, () => HardwareMapping.Clean("Не указано")).Should().Be("Не указано");   // only the known English placeholders are filler
        Under(culture, () => HardwareMapping.Clean("TO BE FILLED BY O.E.M.")).Should().BeNull();
        Under(culture, () => HardwareMapping.FullBuild("26100", 4061)).Should().Be("26100.4061");
        Under(culture, () => HardwareMapping.FullBuild("26100", 1234567)).Should().Be("26100.1234567");   // no digit grouping
        Under(culture, () => HardwareMapping.WindowsVersion("24H2", null)).Should().Be("24H2");
        Under(culture, () => HardwareMapping.OsArchitecture("64-разрядная", 9)).Should().Be("x64");
        Under(culture, () => HardwareMapping.OsArchitecture("64-բիթանոց", 9)).Should().Be("x64");
    }

    [Theory, MemberData(nameof(Cultures))]
    public void Fast_startup_basis_does_not_depend_on_culture(string culture)
    {
        Under(culture, () => HardwareMapping.FastStartup(1)).Should().BeTrue();
        Under(culture, () => HardwareMapping.FastStartup(0)).Should().BeFalse();
        Under(culture, () => HardwareMapping.FastStartup(null)).Should().BeNull();
        Under(culture, () => new OsInventory(null, null, null, null, null, null, null, true).UptimeBasis).Should().Be("fullBoot");
        Under(culture, () => new OsInventory(null, null, null, null, null, null, null, false).UptimeBasis).Should().Be("boot");
        Under(culture, () => new OsInventory(null, null, null, null, null, null, null).UptimeBasis).Should().Be("boot");
    }

    [Theory, MemberData(nameof(Cultures))]
    public void The_overview_serializes_numbers_and_dates_invariantly(string culture)
    {
        var at = new DateTime(2026, 10, 14, 9, 5, 7, DateTimeKind.Utc);
        var json = Under(culture, () => JsonSerializer.Serialize(
            new EndpointOverviewDto(true, at, at, false, null, null, new LiveSampleDto(12.5, 17179869184, 8589934592, 8589934592, 50.25, at))));
        json.Should().Contain("12.5").And.Contain("50.25").And.Contain("2026-10-14T09:05:07Z").And.NotContain("12,5");
        var back = Under(culture, () => JsonSerializer.Deserialize<EndpointOverviewDto>(json));
        back!.Live!.CpuLoadPercent.Should().Be(12.5);
        back.Live.SampledUtc.Should().Be(at);
    }

    [Fact]
    public void A_stored_snapshot_round_trips_with_the_fast_startup_flag()
    {
        var os = new OsInventory("Windows 11 Pro", "Professional", "24H2", "26100.4061", "x64", null, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), true);
        var dto = new HardwareInventoryDto("Complete", DateTime.UtcNow, false, false, [], os, null, null, null, null, null);
        var back = JsonSerializer.Deserialize<HardwareInventoryDto>(JsonSerializer.Serialize(dto));
        back!.Os!.FastStartupEnabled.Should().BeTrue();
        back.Os.UptimeBasis.Should().Be("fullBoot");
        // an older stored snapshot (before this field existed) still loads and reads as "boot"
        var old = JsonSerializer.Deserialize<HardwareInventoryDto>(
            "{\"Status\":\"Complete\",\"Os\":{\"ProductName\":\"Windows 10\",\"BootTimeUtc\":\"2026-10-01T00:00:00Z\"},\"Errors\":[]}");
        old!.Os!.FastStartupEnabled.Should().BeNull();
        old.Os.UptimeBasis.Should().Be("boot");
    }
}
