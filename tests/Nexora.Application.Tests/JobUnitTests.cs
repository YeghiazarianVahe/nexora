using System.Text.Json;
using FluentAssertions;
using Nexora.Application.Jobs;
using Nexora.Contracts;
using Nexora.Domain;

namespace Nexora.Application.Tests;

public sealed class ParameterValidatorTests
{
    private static Dictionary<string, JsonElement> J(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    private static ActionDefinition Def(string code) => ActionCatalog.Find(code)!;

    [Fact]
    public void Defaults_are_filled_in_and_values_come_back_as_strings()
    {
        var (values, error) = ParameterValidator.Validate(Def("demo.simulate"), J("""{"steps": 3}"""));
        error.Should().BeNull();
        values.Should().Equal(new Dictionary<string, string> { ["steps"] = "3", ["delayMs"] = "400", ["outcome"] = "success" });
    }

    [Theory]
    [InlineData("""{"steps": 0}""")]
    [InlineData("""{"steps": 21}""")]
    [InlineData("""{"steps": 1.5}""")]
    [InlineData("""{"steps": "3"}""")]
    [InlineData("""{"outcome": "nope"}""")]
    [InlineData("""{"surprise": 1}""")]
    public void Out_of_schema_input_is_rejected(string json) =>
        ParameterValidator.Validate(Def("demo.simulate"), J(json)).Error.Should().NotBeNull();

    [Fact]
    public void Required_parameters_must_be_present()
    {
        ParameterValidator.Validate(Def("svc.restart"), J("{}")).Error.Should().Contain("required");
        ParameterValidator.Validate(Def("svc.restart"), J("""{"serviceName": "Spooler"}""")).Values!["serviceName"].Should().Be("Spooler");
    }

    [Theory]
    [InlineData("RpcSs")]
    [InlineData("rpcss")]
    [InlineData("WinDefend")]
    public void Critical_services_are_denied(string name) =>
        ParameterValidator.Validate(Def("svc.stop"), J($$"""{"serviceName": "{{name}}"}""")).Error.Should().Contain("protected");

    [Theory]
    [InlineData("lsass.exe")]
    [InlineData("CSRSS")]
    [InlineData("winlogon.exe")]
    public void System_processes_are_denied(string name) =>
        ParameterValidator.Validate(Def("proc.kill"), J($$"""{"pid": 600, "processName": "{{name}}"}""")).Error.Should().Contain("protected");

    [Theory]
    [InlineData("a;b")]
    [InlineData("a\"b")]
    [InlineData("a\nb")]
    public void Injection_characters_are_rejected_in_names(string name)
    {
        var json = JsonSerializer.Serialize(new { serviceName = name });
        ParameterValidator.Validate(Def("svc.start"), J(json)).Error.Should().NotBeNull();
    }

    [Fact]
    public void A_dism_source_must_be_a_unc_path_and_kb_lists_must_look_like_kbs()
    {
        ParameterValidator.Validate(Def("repair.dism.restore"), J("""{"source": "\\\\fileserver\\sources\\sxs\\install.wim", "limitAccess": true}""")).Error.Should().BeNull();
        ParameterValidator.Validate(Def("repair.dism.restore"), J("""{"source": "C:\\local\\install.wim"}""")).Error.Should().NotBeNull();
        ParameterValidator.Validate(Def("update.install"), J("""{"kbs": ["KB5040442", "kb5039212"]}""")).Values!["kbs"].Should().Be("KB5040442,kb5039212");
        ParameterValidator.Validate(Def("update.install"), J("""{"kbs": ["KB5040442", "; calc"]}""")).Error.Should().NotBeNull();
    }

    [Fact]
    public void The_hash_is_independent_of_order_so_a_confirmation_matches_its_request()
    {
        var a = new Dictionary<string, string> { ["x"] = "1", ["y"] = "2" };
        var b = new Dictionary<string, string> { ["y"] = "2", ["x"] = "1" };
        ParameterValidator.Hash(a).Should().Be(ParameterValidator.Hash(b));
        ParameterValidator.Hash(a).Should().NotBe(ParameterValidator.Hash(new Dictionary<string, string> { ["x"] = "1", ["y"] = "3" }));
    }
}

public sealed class CatalogTests
{
    [Fact]
    public void Risk_rules_follow_the_policy()
    {
        foreach (var a in ActionCatalog.All.Where(a => !a.Demo))
        {
            a.NeedsConfirmation.Should().Be(a.Risk >= RiskLevel.Medium, a.Code);
            a.NeedsReason.Should().Be(a.Risk >= RiskLevel.High, a.Code);
            if (a.Risk == RiskLevel.Critical) a.MinRole.Should().Be(Role.Admin, $"{a.Code} is Critical");
        }
    }

    [Fact]
    public void The_specified_actions_exist_with_their_risk_role_and_timeouts()
    {
        void Check(string code, RiskLevel risk, Role role, int minutes)
        {
            var a = ActionCatalog.Find(code) ?? throw new Xunit.Sdk.XunitException("missing " + code);
            (a.Risk, a.MinRole, (int)a.Timeout.TotalMinutes).Should().Be((risk, role, minutes), code);
        }
        Check("power.restart", RiskLevel.High, Role.Operator, 5);
        Check("repair.sfc", RiskLevel.Medium, Role.Operator, 60);
        Check("repair.dism.restore", RiskLevel.High, Role.Operator, 120);
        Check("disk.chkdsk.fix", RiskLevel.Critical, Role.Admin, 5);
        Check("update.scan", RiskLevel.Low, Role.Operator, 30);
        Check("update.install", RiskLevel.Critical, Role.Admin, 120);
        Check("script.run", RiskLevel.Critical, Role.Admin, 30);
        Check("disk.smart", RiskLevel.Low, Role.Viewer, 5);
    }

    [Fact]
    public void Heavy_actions_are_the_repair_chkdsk_and_update_ones()
    {
        ActionCatalog.All.Where(a => a.Heavy && !a.Demo).Select(a => a.Code).Should().BeEquivalentTo(
            ["repair.sfc", "repair.dism.check", "repair.dism.scan", "repair.dism.restore", "repair.dism.cleanup", "disk.chkdsk.scan",
             "disk.chkdsk.fix", "update.scan", "update.install", "update.reset", "diag.bundle"]);
    }

    [Fact]
    public void Exit_codes_map_to_states_with_a_sensible_fallback()
    {
        var chk = ActionCatalog.Find("disk.chkdsk.scan")!;
        chk.MapExit(0).State.Should().Be(TargetState.Succeeded);
        chk.MapExit(1).State.Should().Be(TargetState.Warning);
        chk.MapExit(3).State.Should().Be(TargetState.Failed);
        chk.MapExit(77).Should().Be((TargetState.Failed, "Exited with code 77."));
        ActionCatalog.Find("repair.dism.check")!.MapExit(-2146498529).Summary.Should().Contain("0x800f081f");
    }

    [Fact]
    public void Every_action_uses_one_of_the_allowed_execution_methods_and_none_is_winrm()
    {
        Enum.GetNames<ExecutionMethod>().Should().BeEquivalentTo(["Wmi", "SystemTask", "AdminShare", "Local"]);
    }
}

public sealed class JobStateTests
{
    private static readonly Job J = new() { Status = JobState.Running };

    [Theory]
    [InlineData(new[] { TargetState.Succeeded, TargetState.Succeeded }, JobState.Succeeded)]
    [InlineData(new[] { TargetState.Succeeded, TargetState.Warning }, JobState.Warning)]
    [InlineData(new[] { TargetState.Succeeded, TargetState.Failed }, JobState.PartiallyFailed)]
    [InlineData(new[] { TargetState.Failed, TargetState.TimedOut }, JobState.Failed)]
    [InlineData(new[] { TargetState.Succeeded, TargetState.Skipped }, JobState.Succeeded)]
    [InlineData(new[] { TargetState.Running, TargetState.Succeeded }, JobState.Running)]
    [InlineData(new[] { TargetState.Skipped, TargetState.Skipped }, JobState.Failed)]
    public void The_job_state_is_derived_from_its_targets(TargetState[] targets, JobState expected) =>
        Nexora.Application.Jobs.JobService.ComputeState(J, targets).Should().Be(expected);

    [Fact]
    public void A_cancelled_job_reports_Cancelled_once_every_target_is_final()
    {
        var job = new Job { Status = JobState.Running, CancelRequestedUtc = DateTime.UtcNow };
        JobService.ComputeState(job, [TargetState.Cancelled, TargetState.Succeeded]).Should().Be(JobState.Cancelled);
        JobService.ComputeState(job, [TargetState.Cancelled, TargetState.Running]).Should().Be(JobState.Running);
    }

    [Fact]
    public void A_job_waiting_for_its_helper_stays_waiting_until_something_runs()
    {
        var job = new Job { Status = JobState.WaitingForHelper };
        JobService.ComputeState(job, [TargetState.Queued]).Should().Be(JobState.WaitingForHelper);
        JobService.ComputeState(job, [TargetState.Queued, TargetState.Running]).Should().Be(JobState.Running);
    }
}

public sealed class OutputRedactorTests
{
    [Theory]
    [InlineData("password=Hunter2", "password=***")]
    [InlineData("Password: hunter2 and more", "Password: *** and more")]
    [InlineData("connect --pwd=abc", "connect --pwd=***")]
    [InlineData("api_key=XYZ123", "api_key=***")]
    [InlineData("TOKEN = eyJ.abc", "TOKEN = ***")]
    [InlineData("nothing secret here", "nothing secret here")]
    [InlineData("passwordless login enabled", "passwordless login enabled")]
    public void Secrets_are_masked_and_normal_text_is_left_alone(string input, string expected) =>
        OutputRedactor.Redact(input).Should().Be(expected);
}

public sealed class ConfirmTokenTests
{
    private readonly ConfirmTokens _tokens = new();
    private readonly DateTime _now = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_token_is_valid_once_for_the_exact_request()
    {
        var t = _tokens.Issue("S-1", "power.restart", "T", "P", false, _now).Token;
        _tokens.Consume(t, "S-1", "power.restart", "T", "P", false, _now.AddMinutes(1)).Should().BeTrue();
        _tokens.Consume(t, "S-1", "power.restart", "T", "P", false, _now.AddMinutes(1)).Should().BeFalse("single use");
    }

    [Theory]
    [InlineData("S-2", "power.restart", "T", "P", false)]
    [InlineData("S-1", "power.shutdown", "T", "P", false)]
    [InlineData("S-1", "power.restart", "X", "P", false)]
    [InlineData("S-1", "power.restart", "T", "X", false)]
    [InlineData("S-1", "power.restart", "T", "P", true)]
    public void Any_difference_invalidates_the_token(string sid, string action, string targets, string pars, bool over)
    {
        var t = _tokens.Issue("S-1", "power.restart", "T", "P", false, _now).Token;
        _tokens.Consume(t, sid, action, targets, pars, over, _now).Should().BeFalse();
    }

    [Fact]
    public void Expired_tampered_and_foreign_tokens_are_refused()
    {
        var t = _tokens.Issue("S-1", "a", "T", "P", false, _now).Token;
        _tokens.Consume(t, "S-1", "a", "T", "P", false, _now.AddMinutes(6)).Should().BeFalse();
        _tokens.Consume(t[..^2] + "00", "S-1", "a", "T", "P", false, _now).Should().BeFalse();
        _tokens.Consume(null, "S-1", "a", "T", "P", false, _now).Should().BeFalse();
        _tokens.Consume("garbage", "S-1", "a", "T", "P", false, _now).Should().BeFalse();
        new ConfirmTokens().Consume(t, "S-1", "a", "T", "P", false, _now).Should().BeFalse("another process has another key");
    }
}

public sealed class JobTrustTests : IDisposable
{
    private const string Sid = "S-1-5-21-1-2-3-1000";
    private readonly System.Security.Cryptography.ECDsa _server = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
    private byte[] Pinned => _server.ExportSubjectPublicKeyInfo();
    private readonly DateTime _now = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
    public void Dispose() => _server.Dispose();

    private JobPayload Payload(string action = "demo.simulate", string sid = Sid, TimeSpan? ttl = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), action, "PC-1", sid, new() { ["steps"] = "2" }, 30, _now, _now + (ttl ?? TimeSpan.FromMinutes(2)), "n");

    private static bool Known(string code) => code == "demo.simulate";

    [Fact]
    public void A_job_signed_by_the_pinned_server_key_is_accepted()
    {
        var (payload, error) = JobTrust.Verify(JobTrust.Sign(Payload(), _server), Pinned, Sid, _now.AddSeconds(5), Known);
        error.Should().Be(JobTrust.Error.None);
        payload!.Parameters["steps"].Should().Be("2");
    }

    [Fact]
    public void A_job_signed_by_an_unknown_or_forged_key_is_rejected()
    {
        using var attacker = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var forged = JobTrust.Sign(Payload(), attacker);
        JobTrust.Verify(forged, Pinned, Sid, _now, Known).Error.Should().Be(JobTrust.Error.BadSignature);
        // Even when the attacker also swaps in its own public key, the helper only ever checks the key it has pinned.
        JobTrust.Verify(forged, attacker.ExportSubjectPublicKeyInfo(), Sid, _now, Known).Error.Should().Be(JobTrust.Error.None, "that would only pass for a helper that pinned the attacker, which is what pinning prevents");
    }

    [Fact]
    public void A_tampered_payload_or_garbage_signature_is_rejected()
    {
        var env = JobTrust.Sign(Payload(), _server);
        JobTrust.Verify(env with { Payload = env.Payload.Replace("PC-1", "DC-1") }, Pinned, Sid, _now, Known).Error.Should().Be(JobTrust.Error.BadSignature);
        JobTrust.Verify(env with { Signature = "zz" }, Pinned, Sid, _now, Known).Error.Should().Be(JobTrust.Error.Malformed);
        JobTrust.Verify(env with { Signature = Convert.ToBase64String(new byte[64]) }, Pinned, Sid, _now, Known).Error.Should().Be(JobTrust.Error.BadSignature);
    }

    [Fact]
    public void A_helper_with_no_pinned_key_runs_nothing()
    {
        var env = JobTrust.Sign(Payload(), _server);
        JobTrust.Verify(env, null, Sid, _now, Known).Error.Should().Be(JobTrust.Error.NoPinnedKey);
        JobTrust.Verify(env, [], Sid, _now, Known).Error.Should().Be(JobTrust.Error.NoPinnedKey);
    }

    [Fact]
    public void Expired_wrong_operator_and_unknown_actions_are_refused()
    {
        JobTrust.Verify(JobTrust.Sign(Payload(ttl: TimeSpan.FromSeconds(10)), _server), Pinned, Sid, _now.AddMinutes(5), Known).Error.Should().Be(JobTrust.Error.Expired);
        JobTrust.Verify(JobTrust.Sign(Payload(sid: "S-1-5-21-9"), _server), Pinned, Sid, _now, Known).Error.Should().Be(JobTrust.Error.WrongOperator);
        JobTrust.Verify(JobTrust.Sign(Payload(action: "format.disk"), _server), Pinned, Sid, _now, Known).Error.Should().Be(JobTrust.Error.UnknownAction);
    }

    [Fact]
    public void The_public_key_is_stamped_into_an_exe_read_back_and_replaced_without_growing()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray());
            TrustStamp.Read(path).Key.Should().BeNull();

            TrustStamp.StampInPlace(path, Pinned).Should().BeTrue();
            var (key, original) = TrustStamp.Read(path);
            key.Should().Equal(Pinned);
            original.Should().Be(5000);

            using var other = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var length = new FileInfo(path).Length;
            TrustStamp.StampInPlace(path, other.ExportSubjectPublicKeyInfo()).Should().BeTrue();
            new FileInfo(path).Length.Should().Be(length, "a new key replaces the old stamp");
            TrustStamp.Read(path).Key.Should().Equal(other.ExportSubjectPublicKeyInfo());
            File.ReadAllBytes(path).Take(5000).Should().Equal(Enumerable.Range(0, 5000).Select(i => (byte)i), "the executable itself is untouched");
        }
        finally { File.Delete(path); }
    }
}
