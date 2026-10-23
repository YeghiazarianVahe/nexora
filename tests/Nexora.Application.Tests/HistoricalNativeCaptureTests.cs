using System.Text.Json;
using FluentAssertions;
using Nexora.Application.Services;

namespace Nexora.Application.Tests;
/// <summary>Scoring regression using supplied parsed historical captures, not new raw-log/hardware validation.</summary>
public sealed class HistoricalNativeCaptureTests
{
    [Theory]
    [InlineData("wks-ph65-native-nvme.json",92)]
    [InlineData("wks-tb011-native-nvme.json",56)]
    public void Supplied_parsed_reference_capture_retains_its_validated_endurance_score(string file,int expected)
    {
        var path=Path.Combine(AppContext.BaseDirectory,"Fixtures","HistoricalNative",file);
        var disk=JsonSerializer.Deserialize<DiskRaw>(File.ReadAllText(path),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})!;
        disk.Nvme.Should().NotBeNull();
        DiskHealthEvaluator.Evaluate(disk).HealthPercent.Should().Be(expected);
    }
}
