using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Nexora.Application.Diagnostics;

namespace Nexora.Application.Tests;

/// <summary>The rule language, its loader (fail fast) and the engine, on inline profiles and data. No collector, no PC.</summary>
public sealed class DiagnosticEngineTests
{
    private static readonly string[] Known = ["a", "b"];

    private static DiagProfile Profile(string rulesJson, string collectors = """[{"id":"a","required":true,"timeoutSeconds":10},{"id":"b","required":false,"timeoutSeconds":10}]""") =>
        ProfileLoader.Load([("t.json", $$"""{"id":"t","name":"T","description":"d","collectors":{{collectors}},"rules":{{rulesJson}} }""")], Known)[0];

    private static string[] LoadErrors(string rulesJson, string collectors = """[{"id":"a","required":true,"timeoutSeconds":10}]""")
    {
        var act = () => Profile(rulesJson, collectors);
        return act.Should().Throw<DiagnosticProfileException>().Which.Errors.ToArray();
    }

    private static CollectorResult Ok(string id, string json) => CollectorResult.Ok(id, JsonDocument.Parse(json).RootElement.Clone());

    private static string Rule(string id, string when, string severity = "warning", string confidence = "high", string evidence = "[]", string actions = "[]") =>
        $$"""{"id":"{{id}}","cause":"c {{id}}","severity":"{{severity}}","confidence":"{{confidence}}","when":{{when}},"evidence":{{evidence}},"actions":{{actions}} }""";

    private static DiagnosisResult Run(string when, string data, string evidence = "[]") =>
        new RuleEngine().Evaluate(Profile("[" + Rule("r", when, evidence: evidence) + "]"), [Ok("a", data)]);

    // ---------------- operators and combinators ----------------

    [Theory]
    [InlineData("""{"path":"a.n","op":"gt","value":5}""", """{"n":6}""", true)]
    [InlineData("""{"path":"a.n","op":"gt","value":5}""", """{"n":5}""", false)]
    [InlineData("""{"path":"a.n","op":"gte","value":5}""", """{"n":5}""", true)]
    [InlineData("""{"path":"a.n","op":"lt","value":5}""", """{"n":4.9}""", true)]
    [InlineData("""{"path":"a.n","op":"lte","value":5}""", """{"n":5.1}""", false)]
    [InlineData("""{"path":"a.n","op":"eq","value":5}""", """{"n":5}""", true)]
    [InlineData("""{"path":"a.n","op":"ne","value":5}""", """{"n":6}""", true)]
    [InlineData("""{"path":"a.s","op":"eq","value":"stopped"}""", """{"s":"Stopped"}""", true)]
    [InlineData("""{"path":"a.s","op":"in","value":["x","Y"]}""", """{"s":"y"}""", true)]
    [InlineData("""{"path":"a.s","op":"in","value":["x","y"]}""", """{"s":"z"}""", false)]
    [InlineData("""{"path":"a.s","op":"contains","value":"ONE"}""", """{"s":"someone"}""", true)]
    [InlineData("""{"path":"a.l","op":"contains","value":"x"}""", """{"l":["a","x"]}""", true)]
    [InlineData("""{"path":"a.s","op":"exists"}""", """{"s":"x"}""", true)]
    [InlineData("""{"path":"a.s","op":"exists"}""", """{"t":"x"}""", false)]
    [InlineData("""{"path":"a.s","op":"exists","value":false}""", """{"t":"x"}""", true)]
    [InlineData("""{"path":"a.l","op":"countGt","value":2}""", """{"l":[1,2,3]}""", true)]
    [InlineData("""{"path":"a.l","op":"countGt","value":3}""", """{"l":[1,2,3]}""", false)]
    [InlineData("""{"path":"a.items[*].p","op":"anyMatch","value":"^\\\\\\\\"}""", """{"items":[{"p":"C:\\x"},{"p":"\\\\srv\\share"}]}""", true)]
    [InlineData("""{"path":"a.items[*].p","op":"anyMatch","value":"^\\\\\\\\"}""", """{"items":[{"p":"C:\\x"}]}""", false)]
    [InlineData("""{"path":"a.b","op":"eq","value":true}""", """{"b":true}""", true)]
    [InlineData("""{"path":"a.n","op":"gt","value":10}""", """{"n":"11.5"}""", true)]
    public void Operators(string when, string data, bool fires) =>
        Run(when, data).Findings.Any().Should().Be(fires);

    [Fact]
    public void All_any_and_not_combine_three_valued()
    {
        const string data = """{"x":1,"y":2}""";
        var t = """{"path":"a.x","op":"eq","value":1}""";
        var f = """{"path":"a.x","op":"eq","value":9}""";
        Run($$"""{"all":[{{t}},{"path":"a.y","op":"eq","value":2}]}""", data).Findings.Should().HaveCount(1);
        Run($$"""{"all":[{{t}},{{f}}]}""", data).Findings.Should().BeEmpty();
        Run($$"""{"any":[{{f}},{{t}}]}""", data).Findings.Should().HaveCount(1);
        Run($$"""{"any":[{{f}},{{f}}]}""", data).Findings.Should().BeEmpty();
        Run($$"""{"not":{{f}} }""", data).Findings.Should().HaveCount(1);
        Run($$"""{"not":{{t}} }""", data).Findings.Should().BeEmpty();
    }

    [Fact]
    public void Wildcards_step_into_array_elements_and_a_field_filter_narrows_them()
    {
        const string data = """{"files":[{"kind":"ost","size":10,"onNet":false},{"kind":"pst","size":99,"onNet":true},{"kind":"pst","size":1,"onNet":false}]}""";
        Run("""{"path":"a.files[*].size","op":"gt","value":50}""", data).Findings.Should().HaveCount(1);
        Run("""{"path":"a.files[kind=ost].size","op":"gt","value":50}""", data).Findings.Should().BeEmpty("only the OST is looked at");
        Run("""{"path":"a.files[kind=pst].size","op":"gt","value":50}""", data).Findings.Should().HaveCount(1);
        Run("""{"path":"a.files[onNet=true].kind","op":"eq","value":"pst"}""", data).Findings.Should().HaveCount(1);
        Run("""{"path":"a.files[kind=ost].size","op":"gt","value":50}""", """{"files":[]}""").Findings.Should().BeEmpty("an empty array matches nothing");
    }

    // ---------------- missing and mistyped data: never throws, never fires ----------------

    [Theory]
    [InlineData("""{"path":"a.nope","op":"gt","value":1}""")]
    [InlineData("""{"path":"a.nope.deeper","op":"eq","value":1}""")]
    [InlineData("""{"path":"a.s","op":"gt","value":1}""")]                // text compared as a number
    [InlineData("""{"path":"a.l","op":"gt","value":1}""")]                // array compared as a number
    [InlineData("""{"path":"a.n","op":"anyMatch","value":"x"}""")]        // number matched as text
    [InlineData("""{"path":"a.n","op":"countGt","value":0}""")]           // not an array
    [InlineData("""{"not":{"path":"a.nope","op":"gt","value":1}}""")]     // not(unknown) is still unknown
    [InlineData("""{"not":{"path":"a.s","op":"gt","value":1}}""")]
    [InlineData("""{"path":"a.items[*].x","op":"gt","value":1}""")]       // wildcard over elements without the field
    [InlineData("""{"path":"a.n[*].x","op":"gt","value":1}""")]           // wildcard on a non-array
    public void Missing_or_mistyped_data_never_fires_and_never_throws(string when)
    {
        var act = () => Run(when, """{"s":"text","n":3,"l":[1,2],"items":[{"y":1}]}""");
        act.Should().NotThrow();
        act().Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_missing_property_is_unknown_but_an_empty_array_is_a_definite_no()
    {
        var engine = new RuleEngine();
        var p = Profile("[" + Rule("r", """{"not":{"path":"a.items[*].v","op":"gt","value":1}}""") + "]");
        engine.Evaluate(p, [Ok("a", """{"items":[]}""")]).Findings.Should().HaveCount(1, "no element exceeds 1, and that is a real answer");
        engine.Evaluate(p, [Ok("a", """{}""")]).Findings.Should().BeEmpty("nothing is known");
    }

    [Fact]
    public void Numbers_are_read_with_the_invariant_culture_whatever_the_machine_locale()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var r = Run("""{"path":"a.n","op":"gt","value":1.4}""", """{"n":"1.5"}""", """[{"label":"n","path":"a.n","format":"number"}]""");
            r.Findings.Should().HaveCount(1);
            r.Findings[0].Evidence[0].Value.Should().Be("1.5");
            Run("""{"path":"a.n","op":"gt","value":1.4}""", """{"n":"1,5"}""").Findings.Should().BeEmpty("a comma is not a decimal point in the wire format");
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    // ---------------- bounds ----------------

    [Fact]
    public void A_huge_array_is_cut_not_walked_and_stays_fast()
    {
        var data = "{\"n\":[" + string.Join(",", Enumerable.Range(0, 200_000)) + "]}";
        using var doc = JsonDocument.Parse(data);
        DiagPath.TryParse("a.n[*]", out var path, out _).Should().BeTrue();
        var sw = Stopwatch.StartNew();
        var values = path.Resolve(doc.RootElement);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        values.Values.Count.Should().Be(DiagLimits.MaxNodesPerPath);
        values.Truncated.Should().BeTrue();
        Run("""{"path":"a.n[*]","op":"gt","value":1999}""", data).Findings.Should().BeEmpty("elements past the cap are not looked at");
    }

    [Fact]
    public void A_regex_that_would_backtrack_forever_is_cut_by_its_timeout_and_does_not_fire()
    {
        var text = new string('a', 400) + "!";
        var sw = Stopwatch.StartNew();
        var r = Run("""{"path":"a.s","op":"anyMatch","value":"^(a+)+$"}""", $$"""{"s":"{{text}}"}""");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        r.Findings.Should().BeEmpty();
    }

    [Fact]
    public void Evidence_is_sanitised_and_capped()
    {
        var nasty = "C:\\a\u0007\u001b[31mb\r\nc\u202Eevil" + new string('x', 1000);
        var r = Run("""{"path":"a.p","op":"exists"}""", JsonSerializer.Serialize(new { p = nasty }), """[{"label":"Path","path":"a.p"}]""");
        var value = r.Findings.Single().Evidence.Single().Value;
        value.Length.Should().BeLessThanOrEqualTo(DiagText.MaxValueLength);
        value.Any(char.IsControl).Should().BeFalse();
        value.Should().NotContain("\u202E");
        value.Should().StartWith("C:\\a");
    }

    [Fact]
    public void Evidence_values_and_counts_use_readable_formats()
    {
        var r = Run("""{"path":"a.b","op":"gt","value":1}""", """{"b":5368709120,"p":93.456,"l":[1,2,3]}""",
            """[{"label":"B","path":"a.b","format":"bytes"},{"label":"P","path":"a.p","format":"percent"},{"label":"L","path":"a.l","format":"count"}]""");
        r.Findings.Single().Evidence.Select(e => e.Value).Should().Equal("5.0 GB", "93.5 %", "3");
    }

    // ---------------- loader: fail fast ----------------

    [Fact]
    public void An_unknown_collector_id_stops_the_load()
    {
        var act = () => ProfileLoader.Load([("t.json", """{"id":"t","name":"T","collectors":[{"id":"zzz","required":true,"timeoutSeconds":10}],"rules":[]}""")], Known);
        act.Should().Throw<DiagnosticProfileException>().WithMessage("*unknown collector id 'zzz'*");
    }

    [Fact]
    public void An_unknown_catalog_action_code_stops_the_load()
    {
        var errors = LoadErrors("[" + Rule("r", """{"path":"a.x","op":"exists"}""", actions: """[{"kind":"catalog","actionCode":"no.such.action","label":"x"}]""") + "]");
        errors.Should().Contain(e => e.Contains("unknown catalog action code 'no.such.action'"));
    }

    [Fact]
    public void Catalog_action_parameters_are_validated_against_the_catalog()
    {
        LoadErrors("[" + Rule("r", """{"path":"a.x","op":"exists"}""", actions: """[{"kind":"catalog","actionCode":"svc.start","params":{"bogus":1},"label":"x"}]""") + "]")
            .Should().Contain(e => e.Contains("Unknown parameter 'bogus'"));
        LoadErrors("[" + Rule("r", """{"path":"a.x","op":"exists"}""", actions: """[{"kind":"catalog","actionCode":"svc.start","label":"x"}]""") + "]")
            .Should().Contain(e => e.Contains("required"), "svc.start needs a service name");
        Profile("[" + Rule("r", """{"path":"a.x","op":"exists"}""", actions: """[{"kind":"catalog","actionCode":"svc.start","params":{"serviceName":"WSearch"},"label":"x"}]""") + "]")
            .Rules.Single().Actions.Single().ActionCode.Should().Be("svc.start");
    }

    [Fact]
    public void A_demo_catalog_action_cannot_be_recommended()
    {
        LoadErrors("[" + Rule("r", """{"path":"a.x","op":"exists"}""", actions: """[{"kind":"catalog","actionCode":"demo.simulate","label":"x"}]""") + "]")
            .Should().Contain(e => e.Contains("unknown catalog action code"));
    }

    [Fact]
    public void An_unknown_operator_a_bad_regex_and_a_bad_path_are_rejected_at_load()
    {
        LoadErrors("[" + Rule("r1", """{"path":"a.x","op":"between","value":1}""") + "]").Should().Contain(e => e.Contains("unknown operator 'between'"));
        LoadErrors("[" + Rule("r2", """{"path":"a.x[*]","op":"anyMatch","value":"(unclosed"}""") + "]").Should().Contain(e => e.Contains("not a valid regular expression"));
        LoadErrors("[" + Rule("r3", """{"path":"a.x[0]","op":"exists"}""") + "]").Should().Contain(e => e.Contains("bad path"));
        LoadErrors("[" + Rule("r4", """{"path":"a.x.__proto__ ","op":"exists"}""") + "]").Should().Contain(e => e.Contains("bad path"));
        LoadErrors("[" + Rule("r5", """{"path":"zzz.x","op":"exists"}""") + "]").Should().Contain(e => e.Contains("not a collector of this profile"));
        LoadErrors("[" + Rule("r6", """{"path":"a.x","op":"gt","value":"text"}""") + "]").Should().Contain(e => e.Contains("needs a numeric value"));
        LoadErrors("[" + Rule("r7", """{"path":"a.x","op":"in","value":[]}""") + "]").Should().Contain(e => e.Contains("'in'"));
    }

    [Fact]
    public void A_duplicate_rule_id_and_an_unknown_property_are_rejected()
    {
        var one = Rule("same", """{"path":"a.x","op":"exists"}""");
        LoadErrors($"[{one},{one}]").Should().Contain(e => e.Contains("duplicate rule id 'same'"));
        LoadErrors("[" + one.Replace("\"cause\"", "\"shell\":\"calc\",\"cause\"") + "]").Should().Contain(e => e.Contains("unknown property 'shell'"));
        LoadErrors("[" + Rule("x", """{"path":"a.x","op":"exists","script":"1"}""") + "]").Should().Contain(e => e.Contains("unknown property 'script'"));
        LoadErrors("[" + Rule("x", """{"all":[{"path":"a.x","op":"exists"}],"any":[]}""") + "]").Should().Contain(e => e.Contains("cannot be combined"));
    }

    [Fact]
    public void A_condition_nested_too_deep_or_too_large_is_rejected()
    {
        var deep = """{"path":"a.x","op":"exists"}""";
        for (var i = 0; i < DiagLimits.MaxConditionDepth + 1; i++) deep = $$"""{"not":{{deep}} }""";
        LoadErrors("[" + Rule("deep", deep) + "]").Should().Contain(e => e.Contains("nested deeper"));
        const string leaf = """{"path":"a.x","op":"exists"}""";
        var wide = "{\"all\":[" + string.Join(",", Enumerable.Repeat(leaf, 20)) + "]}";
        var wider = "{\"all\":[" + string.Join(",", Enumerable.Repeat(wide, 4)) + "]}";
        LoadErrors("[" + Rule("wide", wider) + "]").Should().Contain(e => e.Contains("more than"));
    }

    [Fact]
    public void A_collector_timeout_out_of_range_and_a_server_only_source_without_a_collector_are_handled()
    {
        LoadErrors("[]", """[{"id":"a","required":true,"timeoutSeconds":999}]""").Should().Contain(e => e.Contains("timeoutSeconds"));
        // server:diskHealth needs no collector entry.
        Profile("[" + Rule("d", """{"path":"server:diskHealth.worstStatus","op":"eq","value":"Critical"}""") + "]").Rules.Should().HaveCount(1);
    }

    // ---------------- engine ----------------

    [Fact]
    public void Findings_are_sorted_by_confidence_times_severity_then_rule_id()
    {
        var rules = "[" + string.Join(",",
            Rule("b-low-info", """{"path":"a.x","op":"exists"}""", "info", "low"),
            Rule("z-high-critical", """{"path":"a.x","op":"exists"}""", "critical", "high"),
            Rule("c-med-warning", """{"path":"a.x","op":"exists"}""", "warning", "medium"),
            Rule("a-med-warning", """{"path":"a.x","op":"exists"}""", "warning", "medium"),
            Rule("m-high-warning", """{"path":"a.x","op":"exists"}""", "warning", "high")) + "]";

        var r = new RuleEngine().Evaluate(Profile(rules), [Ok("a", """{"x":1}""")]);

        r.Findings.Select(f => f.Id).Should().Equal("z-high-critical", "m-high-warning", "a-med-warning", "c-med-warning", "b-low-info");
        r.Findings.Select(f => f.Score).Should().Equal(9, 6, 4, 4, 1);
    }

    [Fact]
    public void An_incomplete_collector_makes_the_result_incomplete_and_its_required_rules_not_evaluated_without_a_crash()
    {
        var rules = "[" + string.Join(",",
            Rule("needs-a", """{"path":"a.x","op":"exists"}"""),
            Rule("needs-b", """{"path":"b.x","op":"exists"}"""),
            Rule("never-fires", """{"not":{"path":"a.x","op":"exists"}}""")) + "]";

        var r = new RuleEngine().Evaluate(Profile(rules), [CollectorResult.Incomplete("a", "time budget", JsonDocument.Parse("""{"x":1}""").RootElement.Clone()), Ok("b", """{"x":1}""")]);

        r.Complete.Should().BeFalse();
        r.NotEvaluated.Should().BeEquivalentTo("needs-a", "never-fires");
        r.Findings.Select(f => f.Id).Should().Equal("needs-b");
        r.Collectors.Should().Contain(new CollectorStatus("a", "incomplete", "time budget"));
        r.Collectors.Should().Contain(new CollectorStatus("b", "ok"));
    }

    [Fact]
    public void A_missing_required_collector_is_insufficient_data_but_a_missing_optional_one_just_stays_silent()
    {
        var rules = "[" + string.Join(",", Rule("needs-a", """{"path":"a.x","op":"exists"}"""), Rule("needs-b", """{"path":"b.x","op":"exists"}""")) + "]";
        var r = new RuleEngine().Evaluate(Profile(rules), []);
        r.NotEvaluated.Should().Equal("needs-a");
        r.Findings.Should().BeEmpty();
        r.Complete.Should().BeFalse();
        r.Collectors.Select(c => c.Status).Should().OnlyContain(s => s == "missing");
    }

    [Fact]
    public void A_server_side_source_that_is_absent_is_not_a_reason_to_report_missing_data()
    {
        var p = Profile("[" + Rule("d", """{"path":"server:diskHealth.worstStatus","op":"eq","value":"Critical"}""") + "]");
        var r = new RuleEngine().Evaluate(p, [Ok("a", "{}"), Ok("b", "{}")]);
        r.Complete.Should().BeTrue();
        r.NotEvaluated.Should().BeEmpty();
        r.Findings.Should().BeEmpty();
        new RuleEngine().Evaluate(p, [Ok("a", "{}"), Ok("b", "{}"), Ok("server:diskHealth", """{"worstStatus":"Critical"}""")]).Findings.Should().HaveCount(1);
    }

    [Fact]
    public void Duplicate_or_null_results_do_not_break_the_engine()
    {
        var p = Profile("[" + Rule("r", """{"path":"a.x","op":"exists"}""") + "]");
        var act = () => new RuleEngine().Evaluate(p, [Ok("a", """{"x":1}"""), Ok("a", "{}"), null!]);
        act.Should().NotThrow();
        act().Findings.Should().HaveCount(1, "the first result of an id wins");
    }

    [Fact]
    public void The_result_is_versioned_and_round_trips_through_json()
    {
        var r = Run("""{"path":"a.x","op":"exists"}""", """{"x":1}""");
        r.SchemaVersion.Should().Be(DiagnosisResult.Schema.Version);
        r.ProfileId.Should().Be("t");
        var json = JsonSerializer.Serialize(r, DiagnosisJson.Options);
        JsonSerializer.Deserialize<DiagnosisResult>(json, DiagnosisJson.Options)!.Findings.Single().Id.Should().Be("r");
        json.Should().Contain("\"schemaVersion\":1").And.Contain("\"confidence\":\"high\"");
    }

    [Fact]
    public void A_rule_that_needs_data_is_not_evaluated_when_that_data_is_empty_or_the_collector_incomplete()
    {
        var rules = "[" + Rule("probe", """{"any":[{"path":"b.targets[*].loss","op":"gt","value":2}]}""").Replace("\"when\"", "\"needs\":[\"b.targets[*]\"],\"when\"") + "]";
        var p = Profile(rules, """[{"id":"a","required":false,"timeoutSeconds":10},{"id":"b","required":true,"timeoutSeconds":10}]""");
        var engine = new RuleEngine();

        engine.Evaluate(p, [Ok("b", """{"targets":[]}""")]).NotEvaluated.Should().Equal("probe");
        engine.Evaluate(p, [Ok("b", "{}")]).NotEvaluated.Should().Equal("probe");
        engine.Evaluate(p, [CollectorResult.Incomplete("b", "no targets")]).NotEvaluated.Should().Equal("probe");
        var fine = engine.Evaluate(p, [Ok("b", """{"targets":[{"loss":0}]}""")]);
        fine.NotEvaluated.Should().BeEmpty();
        fine.Findings.Should().BeEmpty();
        engine.Evaluate(p, [Ok("b", """{"targets":[{"loss":9}]}""")]).Findings.Should().HaveCount(1);
    }

    [Fact]
    public void A_recommended_catalog_action_that_is_not_implemented_is_rejected()
    {
        LoadErrors("[" + Rule("r", """{"path":"a.x","op":"exists"}""", actions: """[{"kind":"catalog","actionCode":"power.wake","label":"x"}]""") + "]")
            .Should().Contain(e => e.Contains("not implemented"));
    }

    [Fact]
    public void Sanitising_removes_NUL_and_unpaired_surrogates_everywhere_and_keeps_real_characters()
    {
        var raw = "{\"a\\u0000b\":\"x\\u0000y\\ud800z \\ud83d\\ude00 é\",\"n\":[1,2.5,{\"k\":\"\\udc00\"}],\"t\":true,\"z\":null}";
        var clean = JsonSanitizer.Clean(JsonDocument.Parse(JsonSanitizer.CleanJsonText(raw)).RootElement);
        clean.GetProperty("ab").GetString().Should().Be("xy�z 😀 é");
        clean.GetProperty("n")[2].GetProperty("k").GetString().Should().Be("�");
        clean.GetProperty("n")[1].GetDouble().Should().Be(2.5);
        clean.GetRawText().Should().NotContain("\\u0000").And.NotContain("\\ud800");
    }

    [Fact]
    public void Join_relates_two_collectors_and_an_empty_other_side_is_a_definite_no()
    {
        var rules = "[" + Rule("low-on-ost-drive", """{"path":"b.vols[*].free","op":"lt","value":10,"join":{"key":"letter","with":"a.files[kind=ost].drive"}}""") + "]";
        var p = Profile(rules);
        var vols = """{"vols":[{"letter":"C","free":50},{"letter":"D:","free":3}]}""";
        new RuleEngine().Evaluate(p, [Ok("a", """{"files":[{"kind":"ost","drive":"D"}]}"""), Ok("b", vols)]).Findings.Should().HaveCount(1);
        new RuleEngine().Evaluate(p, [Ok("a", """{"files":[{"kind":"ost","drive":"C"}]}"""), Ok("b", vols)]).Findings.Should().BeEmpty("the OST is on C, which has room");
        new RuleEngine().Evaluate(p, [Ok("a", """{"files":[{"kind":"pst","drive":"D"}]}"""), Ok("b", vols)]).Findings.Should().BeEmpty("no OST at all");
        new RuleEngine().Evaluate(p, [Ok("a", """{"files":[{"kind":"ost","drive":null}]}"""), Ok("b", vols)]).Findings.Should().BeEmpty("a network OST has no drive letter to compare");
    }
}
