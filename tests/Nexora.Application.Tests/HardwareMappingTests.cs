using FluentAssertions;
using Nexora.Application.Services;

namespace Nexora.Application.Tests;

/// <summary>Mapping from raw WMI values to inventory values: unavailable stays null, nothing is guessed.</summary>
public sealed class HardwareMappingTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("To Be Filled By O.E.M.")]
    [InlineData("Default string")]
    [InlineData("Unknown")]
    [InlineData("Not Specified")]
    [InlineData("00000000")]
    [InlineData("FFFFFFFF")]
    [InlineData("System Product Name")]
    public void Placeholders_and_empty_values_are_unavailable(string? raw) => HardwareMapping.Clean(raw).Should().BeNull();

    [Fact]
    public void Real_values_are_trimmed_and_kept() => HardwareMapping.Clean("  Dell Inc.  ").Should().Be("Dell Inc.");

    [Theory]
    [InlineData(34, null, "DDR5")]
    [InlineData(26, null, "DDR4")]
    [InlineData(35, null, "LPDDR5")]
    [InlineData(0, 24, "DDR3")]
    [InlineData(null, 26, "DDR4")]
    [InlineData(20, 20, "DDR2 FB-DIMM")]   // SMBIOS 20 is not the legacy "DDR"
    [InlineData(0, 20, null)]               // ambiguous legacy value, not guessed
    [InlineData(2, 0, null)]
    [InlineData(null, null, null)]
    public void Memory_technology_uses_SMBIOS_first(int? smbios, int? legacy, string? expected) =>
        HardwareMapping.MemoryTechnology(smbios, legacy).Should().Be(expected);

    [Theory]
    [InlineData(4, 2, 4, 2, 2)]
    [InlineData(4, 4, 4, 4, 0)]
    public void Empty_slots_are_total_minus_occupied(int total, int modules, int slots, int occupied, int empty) =>
        HardwareMapping.Slots(total, modules).Should().Be((slots, occupied, empty));

    [Theory]
    [InlineData(null, 2)]
    [InlineData(0, 2)]
    [InlineData(2, 4)]   // fewer slots than modules is inconsistent
    public void Unknown_or_inconsistent_slot_counts_never_produce_a_false_empty_count(int? total, int modules)
    {
        var (_, occupied, empty) = HardwareMapping.Slots(total, modules);
        occupied.Should().Be(modules);
        empty.Should().BeNull();
    }

    [Fact]
    public void Slot_count_is_unknown_not_zero_when_not_reported() => HardwareMapping.Slots(null, 0).Slots.Should().BeNull();

    [Theory]
    [InlineData("24H2", "2009", "24H2")]
    [InlineData(null, "2009", "2009")]
    [InlineData("", null, null)]
    public void Windows_release_comes_from_the_registry_values_only(string? display, string? releaseId, string? expected) =>
        HardwareMapping.WindowsVersion(display, releaseId).Should().Be(expected);

    [Theory]
    [InlineData("26100", 4061, "26100.4061")]
    [InlineData("26100", null, "26100")]
    [InlineData(null, 4061, null)]
    public void Full_build_needs_the_real_build_number(string? build, int? ubr, string? expected) =>
        HardwareMapping.FullBuild(build, ubr).Should().Be(expected);

    [Theory]
    [InlineData("64-bit", 9, "x64")]
    [InlineData("64-bit", 12, "ARM64")]
    [InlineData("ARM 64-bit Processor", 12, "ARM64")]
    [InlineData("32-bit", 9, "x86")]
    [InlineData(null, 9, "x64")]
    [InlineData(null, null, null)]
    public void OS_architecture(string? os, int? cpu, string? expected) => HardwareMapping.OsArchitecture(os, cpu).Should().Be(expected);

    [Theory]
    [InlineData("GenuineIntel", "Intel")]
    [InlineData("AuthenticAMD", "AMD")]
    [InlineData("Qualcomm Technologies Inc", "Qualcomm Technologies Inc")]
    [InlineData(null, null)]
    public void CPU_vendor(string? raw, string? expected) => HardwareMapping.CpuVendor(raw).Should().Be(expected);
}
