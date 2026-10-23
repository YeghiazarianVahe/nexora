using System.Text.Json;
using FluentAssertions;
using Nexora.Application.Jobs;
using Nexora.Domain;

namespace Nexora.Application.Tests;

public sealed class CatalogS0bTests
{
    private static Dictionary<string, JsonElement> J(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    private static ActionDefinition Def(string code) => ActionCatalog.Find(code)!;

    [Fact]
    public void Proc_kill_keeps_its_code_and_is_shown_as_Stop_process()
    {
        Def("proc.kill").DisplayName.Should().Be("Stop process");
        Def("proc.kill").Description.Should().NotContain("End", "no display text uses the old verb");
    }

    [Theory]
    [InlineData("svc.stop")]
    [InlineData("svc.restart")]
    public void Service_stop_and_restart_take_a_typed_stopDependents_flag_that_defaults_to_false(string code)
    {
        var spec = Def(code).Parameters.Single(p => p.Name == "stopDependents");
        spec.Type.Should().Be(ParamType.Bool);
        spec.Default.Should().Be("false");

        var (values, error) = ParameterValidator.Validate(Def(code), J("""{"serviceName": "Spooler"}"""));
        error.Should().BeNull();
        values!["stopDependents"].Should().Be("false");
        ParameterValidator.Validate(Def(code), J("""{"serviceName": "Spooler", "stopDependents": true}""")).Values!["stopDependents"].Should().Be("true");
        ParameterValidator.Validate(Def(code), J("""{"serviceName": "Spooler", "stopDependents": "yes"}""")).Error.Should().NotBeNull("it must be a real boolean");
    }

    [Fact]
    public void Diag_collect_is_a_low_risk_single_target_read_only_system_task_that_is_not_implemented_yet()
    {
        var d = Def("diag.collect");
        (d.Risk, d.MinRole, d.Method).Should().Be((RiskLevel.Low, Role.Operator, ExecutionMethod.SystemTask));
        d.SupportsBulk.Should().BeFalse();
        d.Heavy.Should().BeFalse();
        d.Implemented.Should().BeFalse("the helper executor (S0c) and the collectors (F5) do not exist yet");
        d.ChecksProtected.Should().BeFalse();
    }

    [Fact]
    public void Diag_collect_collector_ids_are_allow_listed_and_the_list_is_empty_for_now()
    {
        ActionCatalog.CollectorIds.Should().BeEmpty();
        var d = Def("diag.collect");
        ParameterValidator.Validate(d, J("{}")).Error.Should().BeNull();
        ParameterValidator.Validate(d, J("""{"collectorIds": ["anything"]}""")).Error.Should().NotBeNull();
    }

    [Theory]
    [InlineData("""{"exchangeHosts": ["ex01.corp.local", "EX-02"]}""", true)]
    [InlineData("""{"exchangeHosts": ["ex\n01"]}""", false)]
    [InlineData("""{"exchangeHosts": ["ex01;calc"]}""", false)]
    [InlineData("""{"exchangeHosts": ["\\\\ex01"]}""", false)]
    [InlineData("""{"exchangeHosts": ["-ex01"]}""", false)]
    public void Diag_collect_exchange_hosts_must_be_plain_host_names(string json, bool ok) =>
        (ParameterValidator.Validate(Def("diag.collect"), J(json)).Error is null).Should().Be(ok);
}
