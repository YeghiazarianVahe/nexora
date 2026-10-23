using System.Text.Json;
using FluentAssertions;
using Nexora.Application.Diagnostics;

namespace Nexora.Application.Tests;

public sealed class FastDiagnosticsTests
{
    private static readonly CollectorCatalog Collectors = CollectorCatalog.FromAssembly(typeof(DependencyInjection).Assembly);
    private static readonly ProfileCatalog Profiles = ProfileCatalog.FromAssembly(typeof(DependencyInjection).Assembly, Collectors.Ids);
    public static IEnumerable<object[]> Cases()
    {
        yield return new object[] { "high-cpu", "cpu-load", "system-resources", """{"cpu":{"loadPercent":91}}""", """{"cpu":{"loadPercent":90}}""" };
        yield return new object[] { "high-ram", "ram-load", "system-resources", """{"memory":{"usedPercent":91}}""", """{"memory":{"usedPercent":90}}""" };
        yield return new object[] { "low-disk-space", "space-low", "system-resources", """{"disk":{"systemDriveFreePercent":9}}""", """{"disk":{"systemDriveFreePercent":10}}""" };
        yield return new object[] { "disk-health", "disk-warning", "server:diskHealth", """{"worstStatus":"Warning"}""", """{"worstStatus":"OK"}""" };
        yield return new object[] { "network-issue", "no-gateway", "workstation-checks", """{"gatewayConfigured":false}""", """{"gatewayConfigured":true}""" };
        yield return new object[] { "dns-issue", "no-dns", "workstation-checks", """{"dnsConfigured":false}""", """{"dnsConfigured":true}""" };
        yield return new object[] { "dns-issue", "domain-dns", "workstation-checks", """{"domainResolved":false}""", """{"domainResolved":true}""" };
        yield return new object[] { "domain-policy", "not-domain", "workstation-checks", """{"domainJoined":false}""", """{"domainJoined":true}""" };
        yield return new object[] { "domain-policy", "policy-errors", "workstation-checks", """{"policyErrors":1}""", """{"policyErrors":0}""" };
        yield return new object[] { "print-issue", "spooler-stopped", "workstation-checks", """{"spoolerState":"Stopped"}""", """{"spoolerState":"Running"}""" };
        yield return new object[] { "windows-update", "update-reboot", "pending-reboot", """{"pending":true}""", """{"pending":false}""" };
        yield return new object[] { "windows-update", "update-stopped", "workstation-checks", """{"updateState":"Stopped"}""", """{"updateState":"Running"}""" };
        yield return new object[] { "time-sync", "time-stopped", "workstation-checks", """{"timeState":"Stopped"}""", """{"timeState":"Running"}""" };
        yield return new object[] { "user-profile", "temporary-profile", "workstation-checks", """{"temporaryProfiles":1}""", """{"temporaryProfiles":0}""" };
        yield return new object[] { "service-not-running", "auto-stopped", "workstation-checks", """{"automaticStopped":1}""", """{"automaticStopped":0}""" };
        yield return new object[] { "antivirus-update", "defender-stopped", "workstation-checks", """{"defenderState":"Stopped"}""", """{"defenderState":"Running"}""" };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Symptoms_produce_evidence_confidence_and_guidance_and_do_not_match_normal_values(string profileId, string ruleId, string source, string positive, string negative)
    {
        var profile = Profiles.Find(profileId)!;
        var engine = new RuleEngine();
        var good = profile.Collectors.Select(c => CollectorResult.Ok(c.Id, JsonSerializer.SerializeToElement(new { }))).ToList();
        void Put(string json) { good.RemoveAll(c => c.Id == source); good.Add(CollectorResult.Ok(source, JsonDocument.Parse(json).RootElement.Clone())); }
        Put(positive);
        var finding = engine.Evaluate(profile, good).Findings.Single(f => f.Id == ruleId);
        finding.Evidence.Should().NotBeEmpty();
        finding.Confidence.Should().BeOneOf("high", "medium", "low");
        finding.Actions.Should().Contain(a => a.Kind == "manual" && !string.IsNullOrWhiteSpace(a.Instructions));
        Put(negative);
        engine.Evaluate(profile, good).Findings.Should().NotContain(f => f.Id == ruleId);
        // Null is unknown, not an affirmative match for "ne Running".
        Put(positive.Replace(positive[(positive.LastIndexOf(':') + 1)..].TrimEnd('}'), "null"));
        var unknown = engine.Evaluate(profile, good);
        unknown.Findings.Should().NotContain(f => f.Id == ruleId);
        unknown.NotEvaluated.Should().Contain(ruleId);
        unknown.Complete.Should().BeFalse();
    }

    public static IEnumerable<object[]> MissingCases() => Cases().Select(c => new object[] { c[0], c[1] });

    [Theory]
    [MemberData(nameof(MissingCases))]
    public void Missing_collectors_never_generate_a_repair_suggestion(string profileId, string ruleId)
    {
        var result = new RuleEngine().Evaluate(Profiles.Find(profileId)!, []);
        result.Findings.Should().BeEmpty();
        result.NotEvaluated.Should().Contain(ruleId);
        result.Complete.Should().BeFalse();
    }

    [Fact]
    public void New_profiles_and_collectors_are_bounded_and_covered()
    {
        var covered = Cases().Select(c => ((string)c[0], (string)c[1])).ToHashSet();
        var shipped = Profiles.All.Where(p => p.Id is not ("pc-slow" or "outlook-freeze"))
            .SelectMany(p => p.Rules.Select(r => (p.Id, r.Id))).ToHashSet();
        covered.Should().BeEquivalentTo(shipped);
        Profiles.All.Should().HaveCount(15);
        Profiles.All.SelectMany(p => p.Collectors).Should().OnlyContain(c => c.TimeoutSeconds <= 60);
        Collectors.Find("workstation-checks")!.Body.Should().NotContain("Set-Service").And.NotContain("Restart-Service");
    }
}
