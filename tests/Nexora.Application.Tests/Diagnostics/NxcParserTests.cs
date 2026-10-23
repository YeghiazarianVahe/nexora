using System.Text.Json;
using FluentAssertions;
using Nexora.Application.Diagnostics;

namespace Nexora.Application.Tests;

public sealed class NxcParserTests
{
    private static readonly string[] Expect = ["outlook-files", "system-resources"];

    [Fact]
    public void A_well_formed_line_becomes_an_ok_collector_with_its_data()
    {
        var p = NxcParser.Parse(["[helper] collector outlook-files (1 of 2), limit 60 s", """##NXC outlook-files {"schema":1,"files":[]}"""], Expect);
        var r = p.Results.Single();
        (r.Id, r.Status).Should().Be(("outlook-files", "ok"));
        r.Data!.Value.GetProperty("schema").GetInt32().Should().Be(1);
        p.Ignored.Should().Be(0);
    }

    [Fact]
    public void An_incomplete_flag_keeps_the_reason_and_the_partial_data_but_is_not_ok()
    {
        var r = NxcParser.Parse(["""##NXC outlook-files {"incomplete":true,"incompleteReason":"time budget","files":[{"kind":"ost"}]}"""], Expect).Results.Single();
        r.Status.Should().Be("incomplete");
        r.Reason.Should().Be("time budget");
        r.Data.Should().NotBeNull("kept as evidence for the operator, never used by the rules");
        NxcParser.Parse(["""##NXC outlook-files {"incomplete":true,"reason":"timeout"}"""], Expect).Results.Single().Reason.Should().Be("timeout");
    }

    [Fact]
    public void A_failed_flag_is_a_failed_collector()
    {
        var r = NxcParser.Parse(["""##NXC outlook-files {"failed":true,"reason":"access denied"}"""], Expect).Results.Single();
        (r.Status, r.Reason).Should().Be(("failed", "access denied"));
    }

    [Theory]
    [InlineData("##NXC outlook-files {not json")]
    [InlineData("##NXC outlook-files [1,2,3]")]
    [InlineData("##NXC outlook-files \"text\"")]
    [InlineData("##NXC outlook-files")]
    [InlineData("##NXC outlook-files {\"a\":{\"b\":{\"c\":{\"d\":{\"e\":{\"f\":{\"g\":{\"h\":{\"i\":{\"j\":{\"k\":{\"l\":{\"m\":{\"n\":{\"o\":{\"p\":{\"q\":1}}}}}}}}}}}}}}}}}")]
    public void Unparseable_output_makes_that_collector_incomplete_and_nothing_throws(string line)
    {
        var p = NxcParser.Parse([line], Expect);
        p.Results.Should().ContainSingle().Which.Status.Should().Be("incomplete");
    }

    [Fact]
    public void An_oversize_line_makes_its_collector_incomplete()
    {
        var line = "##NXC outlook-files {\"x\":\"" + new string('a', NxcParser.MaxLineLength) + "\"}";
        var r = NxcParser.Parse([line], Expect).Results.Single();
        (r.Status, r.Reason).Should().Be(("incomplete", "output too large"));
        r.Data.Should().BeNull();
    }

    [Fact]
    public void A_line_the_job_truncated_is_incomplete_not_a_failure_of_the_run()
    {
        var r = NxcParser.Parse(["""##NXC outlook-files {"files":[{"path":"C:\Users\x [line truncated]"""], Expect).Results.Single();
        r.Status.Should().Be("incomplete");
    }

    [Fact]
    public void A_duplicate_id_makes_that_collector_incomplete_whichever_line_came_first()
    {
        var p = NxcParser.Parse(["""##NXC outlook-files {"a":1}""", """##NXC system-resources {"b":1}""", """##NXC outlook-files {"a":2}"""], Expect);
        p.Results.Single(r => r.Id == "outlook-files").Should().Match<CollectorResult>(r => r.Status == "incomplete" && r.Reason == "duplicate output" && r.Data == null);
        p.Results.Single(r => r.Id == "system-resources").Status.Should().Be("ok");
    }

    [Theory]
    [InlineData("##NXC pc-slow-processes {\"x\":1}")]            // a collector the profile did not ask for
    [InlineData("##NXC ../../etc {\"x\":1}")]
    [InlineData("##NXC OUTLOOK-FILES {\"x\":1}")]                // ids are exact
    [InlineData("##NXC server:diskHealth {\"worstStatus\":\"OK\"}")] // a PC cannot speak for a server-side source
    [InlineData("##NXC  {\"x\":1}")]
    public void Spoofed_or_unexpected_ids_are_ignored(string line)
    {
        var p = NxcParser.Parse([line], Expect);
        p.Results.Should().BeEmpty();
        p.Ignored.Should().Be(1);
    }

    [Fact]
    public void Lines_that_merely_contain_the_tag_are_not_collector_output()
    {
        var p = NxcParser.Parse(["echo ##NXC outlook-files {}", " ##NXC outlook-files {}", "##NXCoutlook-files {}", "# ##NXC outlook-files {}", ""], Expect);
        p.Results.Should().BeEmpty();
    }

    [Fact]
    public void Evidence_is_capped_and_only_holds_what_delivered_data()
    {
        var big = JsonDocument.Parse("{\"x\":\"" + new string('a', 60_000) + "\"}").RootElement.Clone();
        var results = new[]
        {
            CollectorResult.Ok("a", big), CollectorResult.Ok("b", big), CollectorResult.Ok("c", big), CollectorResult.Ok("d", big), CollectorResult.Ok("e", big),
            CollectorResult.Failed("f", "no"), CollectorResult.Missing("g"),
        };
        var json = DiagnosisCompletion.BuildEvidence(results)!;
        json.Length.Should().BeLessThanOrEqualTo(DiagnosisCompletion.MaxEvidenceBytes);
        var keys = JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name).ToList();
        keys.Should().NotContain(["f", "g"]);
        keys.Should().NotBeEmpty().And.HaveCountLessThan(5);
        DiagnosisCompletion.BuildEvidence([CollectorResult.Missing("g")]).Should().BeNull();
    }

    [Fact]
    public void The_collector_bodies_the_job_carries_match_their_hashes_and_the_helper_limits()
    {
        var catalog = CollectorCatalog.FromAssembly(typeof(DependencyInjection).Assembly);
        var profile = ProfileCatalog.FromAssembly(typeof(DependencyInjection).Assembly, catalog.Ids).All.First();
        var json = DiagnosticJob.CollectorsJson(profile, catalog)!;
        var items = JsonDocument.Parse(json).RootElement.EnumerateArray().ToList();
        items.Should().HaveCount(profile.Collectors.Count);
        foreach (var it in items)
        {
            var body = it.GetProperty("body").GetString()!;
            body.Length.Should().BeLessThanOrEqualTo(20_000);
            it.GetProperty("sha256").GetString().Should().Be(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body))));
            it.GetProperty("timeoutSeconds").GetInt32().Should().BeInRange(1, 120);
            it.GetProperty("id").GetString().Should().MatchRegex("^[a-z0-9][a-z0-9._-]{0,63}$");
        }
        DiagnosticJob.CollectorsJson(profile, new CollectorCatalog([])).Should().BeNull("a collector the server does not have is never sent");
    }
}
