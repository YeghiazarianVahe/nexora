using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nexora.Application.Diagnostics;

namespace Nexora.Application.Tests;

/// <summary>
/// The profiles that ship with the server, run against the synthetic collector fixtures (Fixtures/Diagnostics/&lt;collector&gt;.positive|negative|incomplete.json,
/// written next to the collectors). Every rule has a case that fires it and the all-negative data fires nothing.
/// </summary>
public sealed class ShippedProfileTests
{
    private static readonly CollectorCatalog Collectors = CollectorCatalog.FromAssembly(typeof(DependencyInjection).Assembly);
    private static readonly ProfileCatalog Profiles = ProfileCatalog.FromAssembly(typeof(DependencyInjection).Assembly, Collectors.Ids);
    private static readonly RuleEngine Engine = new();

    private static string FixtureDir
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "Fixtures", "Diagnostics"))) return Path.Combine(dir.FullName, "Fixtures", "Diagnostics");
            throw new DirectoryNotFoundException("Fixtures/Diagnostics");
        }
    }

    private static JsonNode Fixture(string collector, string kind) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureDir, $"{collector}.{kind}.json")))!;

    private static CollectorResult Result(string collector, JsonNode node) =>
        node["incomplete"]?.GetValue<bool>() == true
            ? CollectorResult.Incomplete(collector, "time budget")
            : CollectorResult.Ok(collector, JsonDocument.Parse(node.ToJsonString()).RootElement.Clone());

    private sealed record Case(string Profile, string Rule, string[] Positive, Action<Dictionary<string, JsonNode>>? Mutate = null, string? DiskHealth = null);

    private static DiagnosisResult Evaluate(string profileId, IEnumerable<string> positive, Action<Dictionary<string, JsonNode>>? mutate = null, string? diskHealth = null)
    {
        var profile = Profiles.Find(profileId)!;
        var nodes = profile.Collectors.ToDictionary(c => c.Id, c => Fixture(c.Id, positive.Contains(c.Id) ? "positive" : "negative"));
        mutate?.Invoke(nodes);
        var results = nodes.Select(kv => Result(kv.Key, kv.Value)).ToList();
        if (diskHealth is not null) results.Add(CollectorResult.Ok(DiagPath.DiskHealthSource, JsonDocument.Parse(diskHealth).RootElement.Clone()));
        return Engine.Evaluate(profile, results);
    }

    private const string Critical = """{"worstStatus":"Critical","count":1,"drives":[{"model":"Disk X","status":"Critical","healthPercent":12}]}""";
    private const string Warning = """{"worstStatus":"Warning","count":1,"drives":[{"model":"Disk X","status":"Warning","healthPercent":55}]}""";

    private static readonly Case[] Cases =
    [
        // ----- outlook-freeze
        new("outlook-freeze", "ost-oversize", ["outlook-files"]),
        new("outlook-freeze", "pst-oversize", ["outlook-files"], n => n["outlook-files"]["files"]![1]!["sizeBytes"] = 30_000_000_000),
        new("outlook-freeze", "pst-on-network-share", ["outlook-files"]),
        new("outlook-freeze", "ost-drive-low-space", ["outlook-files", "system-resources"]),
        new("outlook-freeze", "addin-slow-reported", ["outlook-addins"]),
        new("outlook-freeze", "addin-flagged-disabled", ["outlook-addins"]),
        new("outlook-freeze", "addin-load-failed", ["outlook-addins"]),
        new("outlook-freeze", "addin-known-heavy", ["outlook-addins"]),
        new("outlook-freeze", "outlook-crashes", ["outlook-events"]),
        new("outlook-freeze", "outlook-hangs", ["outlook-events"]),
        new("outlook-freeze", "addin-slow-events", ["outlook-events"]),
        new("outlook-freeze", "office-dotnet-errors", [], n => n["outlook-events"]["counts"]!["dotnetOffice"] = 4),
        new("outlook-freeze", "outlook-not-responding-now", ["outlook-process"]),
        new("outlook-freeze", "outlook-memory-high", ["outlook-process"]),
        new("outlook-freeze", "outlook-handles-high", ["outlook-process"]),
        new("outlook-freeze", "outlook-multiple-instances", ["outlook-process"]),
        new("outlook-freeze", "office-build-outdated", ["office-version"]),
        new("outlook-freeze", "office-channel-noncurrent", ["office-version"]),
        new("outlook-freeze", "cached-mode-off", [], n => n["outlook-profile"]["profiles"]!["cachedMode"] = false),
        new("outlook-freeze", "shared-mailboxes-cached", ["outlook-profile"]),
        new("outlook-freeze", "memory-pressure", ["system-resources"]),
        new("outlook-freeze", "cpu-load-high", ["system-resources"]),
        new("outlook-freeze", "search-service-stopped", ["system-resources"]),
        new("outlook-freeze", "search-index-large", ["system-resources"]),
        new("outlook-freeze", "exchange-network-degraded", ["network-probe"]),
        new("outlook-freeze", "drive-health-critical", [], null, Critical),
        new("outlook-freeze", "drive-health-warning", [], null, Warning),

        // ----- pc-slow
        new("pc-slow", "cpu-pressure", ["system-resources"]),
        new("pc-slow", "memory-pressure", ["system-resources"]),
        new("pc-slow", "system-drive-low-space", ["system-resources"]),
        new("pc-slow", "system-drive-almost-full", [], n => n["system-resources"]["disk"]!["systemDriveFreePercent"] = 3),
        new("pc-slow", "cpu-hog-process", ["pc-slow-processes"]),
        new("pc-slow", "memory-hog-process", ["pc-slow-processes"]),
        new("pc-slow", "many-startup-items", ["pc-slow-startup"]),
        new("pc-slow", "auto-services-stopped", ["pc-slow-startup"]),
        new("pc-slow", "many-logon-tasks", ["pc-slow-startup"]),
        new("pc-slow", "pending-reboot", ["pending-reboot"]),
        new("pc-slow", "long-uptime", ["pc-slow-startup"]),
        new("pc-slow", "drive-health-critical", [], null, Critical),
        new("pc-slow", "drive-health-warning", [], null, Warning),
    ];

    public static IEnumerable<object[]> CaseData() => Cases.Select(c => new object[] { c.Profile, c.Rule });

    [Fact]
    public void Both_profiles_load_and_validate_against_the_collectors_and_the_catalog()
    {
        Profiles.All.Select(p => p.Id).Should().Contain(["outlook-freeze", "pc-slow"]);
        Profiles.Find("outlook-freeze")!.Name.Should().Be("Outlook freezes / not responding");
        Profiles.Find("pc-slow")!.Name.Should().Be("PC is slow");
        Profiles.All.SelectMany(p => p.Collectors.Select(c => c.Id)).Distinct().Should().OnlyContain(id => Collectors.Find(id) != null);
        Collectors.Ids.Should().OnlyContain(id => Collectors.Find(id)!.Body.Length <= CollectorCatalog.MaxBodyLength);
    }

    [Fact]
    public void Every_shipped_rule_has_a_case_in_this_file()
    {
        // Focused symptom profiles have positive/negative/unknown coverage in FastDiagnosticsTests.
        var shipped = Profiles.All.Where(p => p.Id is "outlook-freeze" or "pc-slow").SelectMany(p => p.Rules.Select(r => (p.Id, r.Id))).ToHashSet();
        var covered = Cases.Select(c => (c.Profile, c.Rule)).ToHashSet();
        covered.Should().BeEquivalentTo(shipped, "a new rule needs a positive case (and the shared negative case covers its silence)");
    }

    [Theory]
    [MemberData(nameof(CaseData))]
    public void Each_rule_fires_on_its_positive_fixture(string profile, string rule)
    {
        var c = Cases.Single(x => x.Profile == profile && x.Rule == rule);
        var result = Evaluate(profile, c.Positive, c.Mutate, c.DiskHealth);
        result.Findings.Select(f => f.Id).Should().Contain(rule);
        result.Findings.Single(f => f.Id == rule).Cause.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("outlook-freeze")]
    [InlineData("pc-slow")]
    public void The_negative_fixtures_fire_no_rule_at_all_and_the_result_is_complete(string profile)
    {
        var result = Evaluate(profile, []);
        result.Findings.Should().BeEmpty();
        result.NotEvaluated.Should().BeEmpty();
        result.Complete.Should().BeTrue();
        result.Collectors.Should().OnlyContain(c => c.Status == "ok");
    }

    [Theory]
    [InlineData("outlook-freeze")]
    [InlineData("pc-slow")]
    public void Healthy_server_side_drive_health_adds_nothing(string profile) =>
        Evaluate(profile, [], null, """{"worstStatus":"OK","count":1,"drives":[{"model":"Disk X","status":"OK","healthPercent":99}]}""").Findings.Should().BeEmpty();

    [Fact]
    public void The_OST_drive_rule_follows_the_drive_letter_of_the_OST_not_any_low_volume()
    {
        // The OST is on D; C is the one that is nearly full. Nothing is wrong with the OST drive.
        var result = Evaluate("outlook-freeze", ["system-resources"], n =>
        {
            var vols = n["system-resources"]["disk"]!["volumes"]!.AsArray();
            n["outlook-files"]["files"]![0]!["driveLetter"] = "D";
            vols.Add(JsonNode.Parse("""{"letter":"D","sizeBytes":1000000,"freeBytes":900000,"freePercent":90}"""));
        });
        result.Findings.Select(f => f.Id).Should().NotContain("ost-drive-low-space");
    }

    [Fact]
    public void Positive_findings_carry_evidence_and_valid_recommended_actions()
    {
        var f = Evaluate("outlook-freeze", ["outlook-files", "system-resources"]).Findings;
        var ost = f.Single(x => x.Id == "ost-oversize");
        ost.Evidence.Should().Contain(e => e.Label == "OST size" && e.Value.EndsWith("GB"));
        ost.Actions.Should().OnlyContain(a => a.Kind == "manual" && !string.IsNullOrEmpty(a.Instructions));
        var low = f.Single(x => x.Id == "ost-drive-low-space");
        low.Evidence.Single().Value.Should().Be("6 %");
        low.Actions.Should().Contain(a => a.Kind == "catalog" && a.ActionCode == "disk.cleanup");
        f.Select(x => x.Score).Should().BeInDescendingOrder();
    }

    [Theory]
    [InlineData("outlook-freeze", "outlook-files")]
    [InlineData("outlook-freeze", "outlook-addins")]
    [InlineData("outlook-freeze", "outlook-events")]
    [InlineData("outlook-freeze", "outlook-process")]
    [InlineData("outlook-freeze", "system-resources")]
    [InlineData("outlook-freeze", "network-probe")]
    [InlineData("pc-slow", "system-resources")]
    [InlineData("pc-slow", "pc-slow-processes")]
    public void An_incomplete_required_collector_gives_an_incomplete_result_with_those_rules_not_evaluated(string profileId, string collector)
    {
        var profile = Profiles.Find(profileId)!;
        var all = profile.Collectors.Select(c => Result(c.Id, Fixture(c.Id, c.Id == collector ? "incomplete" : "positive"))).ToList();

        var result = Engine.Evaluate(profile, all);

        result.Complete.Should().BeFalse();
        result.Collectors.Single(c => c.Id == collector).Status.Should().Be("incomplete");
        result.NotEvaluated.Should().NotBeEmpty();
        var readsIt = profile.Rules.Where(r => ConditionEvaluator.Sources(r.When).Contains(collector)).Select(r => r.Id).ToList();
        result.NotEvaluated.Should().BeEquivalentTo(readsIt);
        result.Findings.Select(f => f.Id).Should().NotContain(readsIt);
    }

    [Theory]
    [InlineData("outlook-freeze")]
    [InlineData("pc-slow")]
    public void An_optional_collector_that_is_incomplete_only_silences_its_rules(string profileId)
    {
        var profile = Profiles.Find(profileId)!;
        foreach (var optional in profile.Collectors.Where(c => !c.Required))
        {
            var all = profile.Collectors.Select(c => Result(c.Id, Fixture(c.Id, c.Id == optional.Id ? "incomplete" : "positive"))).ToList();
            var result = Engine.Evaluate(profile, all);
            result.Complete.Should().BeFalse();
            result.NotEvaluated.Should().BeEmpty();
            result.Findings.Should().NotBeEmpty();
        }
    }

    [Fact]
    public void Nothing_delivered_at_all_is_a_result_not_a_crash()
    {
        foreach (var p in Profiles.All)
        {
            var result = Engine.Evaluate(p, []);
            result.Complete.Should().BeFalse();
            result.Findings.Should().BeEmpty();
            result.Collectors.Should().HaveCount(p.Collectors.Count).And.OnlyContain(c => c.Status == "missing");
        }
    }

    [Fact]
    public void Every_recommended_catalog_action_is_a_real_non_demo_catalog_entry()
    {
        foreach (var a in Profiles.All.SelectMany(p => p.Rules).SelectMany(r => r.Actions).Where(a => a.Kind == "catalog"))
        {
            var def = Nexora.Application.Jobs.ActionCatalog.Find(a.ActionCode);
            def.Should().NotBeNull(a.ActionCode);
            def!.Demo.Should().BeFalse();
        }
    }
}
