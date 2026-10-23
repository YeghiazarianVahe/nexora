using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nexora.Application.Jobs;
using Nexora.Application.Services;
using Nexora.Contracts;
using Nexora.Domain;

namespace Nexora.Application.Tests;

/// <summary>F2: protected services, risk escalation from dependents, the allowed-actions table and the bounded service reader. No PCs, no WMI.</summary>
public sealed class ServiceControlTests
{
    private sealed class FakeSettings(PlatformSettings s) : ISettingsService
    {
        public Task<PlatformSettings> GetAsync(CancellationToken ct = default) => Task.FromResult(s);
        public Task<Result<PlatformSettings>> UpdateAsync(Actor actor, PlatformSettings settings, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeReader : IServiceReader
    {
        public int Calls;
        public Func<string, ServiceRead<IReadOnlyList<DependentService>>> Dependents { get; set; } = _ => new([], null);
        public Task<ServiceRead<IReadOnlyList<ServiceInfo>>> ReadServicesAsync(string host, TimeSpan maxAge, CancellationToken ct) =>
            Task.FromResult(new ServiceRead<IReadOnlyList<ServiceInfo>>([], null));
        public Task<ServiceRead<IReadOnlyList<DependentService>>> ReadDependentsAsync(string host, string serviceName, TimeSpan maxAge, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(Dependents(host));
        }
        public (IReadOnlyList<ServiceInfo> Services, DateTime AtUtc)? LastKnownServices(string host) => null;
    }

    private static ManagedEndpoint Pc(string host = "pc1", bool online = true) => new() { Id = Guid.NewGuid(), Hostname = host, Online = online, OsCaption = "Microsoft Windows 11 Pro" };
    private static ServiceRead<IReadOnlyList<DependentService>> Deps(params (string, string)[] d) => new(d.Select(x => new DependentService(x.Item1, x.Item2)).ToList(), null);
    private static readonly ServiceRead<IReadOnlyList<DependentService>> Failed = new(null, ServiceReader.ReadFailed);
    private static Dictionary<string, string> P(string name = "Spooler") => new() { ["serviceName"] = name, ["stopDependents"] = "false" };

    private static ServiceRiskEscalator Escalator(FakeReader r) => new(r, NullLogger<ServiceRiskEscalator>.Instance);
    private static ServiceGuard Guard(FakeReader? r = null, params string[] adminList) =>
        new(new FakeSettings(new PlatformSettings { ProtectedServices = [.. adminList] }), r ?? new FakeReader());

    // ---------- the protected floor and the Admin list

    [Theory]
    [InlineData("RpcSs")]
    [InlineData("rpcss")]
    [InlineData("  RpcSs  ")]
    [InlineData("WINDEFEND")]
    [InlineData("DcomLaunch")]
    [InlineData("LSM")]
    [InlineData("EventLog")]
    [InlineData("Netlogon")]
    public async Task The_fixed_floor_is_protected_ignoring_case_and_surrounding_spaces(string name) =>
        (await Guard().IsProtectedAsync(name)).Should().BeTrue();

    [Theory]
    [InlineData("MyCorpAgent")]
    [InlineData("  mycorpagent ")]
    [InlineData("MYCORPAGENT")]
    public async Task The_admin_list_is_protected_ignoring_case_and_surrounding_spaces(string name) =>
        (await Guard(null, "MyCorpAgent").IsProtectedAsync(name)).Should().BeTrue();

    [Theory]
    [InlineData("Spooler")]
    [InlineData("Dhcp")]
    [InlineData("W32Time")]
    [InlineData("BITS")]
    [InlineData("wuauserv")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Services_operators_routinely_restart_are_not_protected(string? name) =>
        (await Guard().IsProtectedAsync(name)).Should().BeFalse();

    [Fact]
    public void The_floor_holds_the_required_names_and_no_routine_service()
    {
        ProtectedNames.Services.Should().Contain(["RpcSs", "DcomLaunch", "LSM", "WinDefend", "EventLog", "Netlogon", "RpcEptMapper", "SamSs", "Winmgmt", "BFE", "mpssvc", "CryptSvc", "LanmanServer", "LanmanWorkstation", "Dnscache"]);
        ProtectedNames.Services.Should().Contain(["NlaSvc", "netprofm", "AppIDSvc", "EventSystem", "Wcmsvc", "Sense", "WdNisSvc", "wscsvc", "SecurityHealthService", "Sysmon", "Sysmon64"]);
        ProtectedNames.Services.Should().NotContain(["Spooler", "Dhcp", "W32Time", "BITS", "wuauserv", "TermService"]);
    }

    // ---------- settings: normalisation and validation

    [Fact]
    public void The_user_list_is_trimmed_deduplicated_and_loses_floor_entries()
    {
        var s = new PlatformSettings { ProtectedServices = [" Foo ", "foo", "FOO", "Bar", "rpcss", "  ", ""] };

        s.Normalize();

        s.ProtectedServices.Should().Equal("Foo", "Bar");
        s.Validate().Should().BeNull();
    }

    [Theory]
    [InlineData("bad;name")]
    [InlineData("a\"b")]
    [InlineData("a\nb")]
    [InlineData("\\\\host\\x")]
    public void Invalid_service_names_are_refused_by_the_settings_validation(string name)
    {
        var s = new PlatformSettings { ProtectedServices = [name] };
        s.Normalize();
        s.Validate().Should().Contain("ProtectedServices");
    }

    [Fact]
    public void The_user_list_is_capped()
    {
        var ok = new PlatformSettings { ProtectedServices = Enumerable.Range(0, ServiceNameRules.MaxProtected).Select(i => $"Svc{i}").ToList() };
        ok.Normalize();
        ok.Validate().Should().BeNull();

        var tooMany = new PlatformSettings { ProtectedServices = Enumerable.Range(0, ServiceNameRules.MaxProtected + 1).Select(i => $"Svc{i}").ToList() };
        tooMany.Normalize();
        tooMany.Validate().Should().Contain("ProtectedServices");
    }

    [Fact]
    public void The_floor_is_read_only_on_the_settings_object_and_a_client_value_for_it_is_ignored()
    {
        var s = JsonSerializer.Deserialize<PlatformSettings>("""{"protectedServices":["X"],"protectedServicesFloor":[]}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        s.ProtectedServices.Should().Equal("X");
        s.ProtectedServicesFloor.Should().BeEquivalentTo(ProtectedNames.Services);
        typeof(PlatformSettings).GetProperty(nameof(PlatformSettings.ProtectedServicesFloor))!.SetMethod.Should().BeNull();
    }

    [Fact]
    public void A_stored_row_from_before_this_setting_still_loads_with_an_empty_list()
    {
        var s = JsonSerializer.Deserialize<PlatformSettings>("""{"PollIntervalSeconds":60}""")!;
        s.ProtectedServices.Should().BeEmpty();
    }

    // ---------- allowed[] table

    [Theory]
    [InlineData("Running", "Auto", false, false, "stop,restart")]
    [InlineData("Running", "Manual", false, false, "stop,restart")]
    [InlineData("Running", "Disabled", false, false, "stop,restart")]
    [InlineData("Stopped", "Auto", false, false, "start")]
    [InlineData("Stopped", "Manual", false, false, "start")]
    [InlineData("Stopped", "Disabled", false, false, "")]
    [InlineData("Paused", "Auto", false, false, "stop")]
    [InlineData("StartPending", "Auto", false, false, "stop")]
    [InlineData("StopPending", "Auto", false, false, "")]
    [InlineData("ContinuePending", "Auto", false, false, "")]
    [InlineData("PausePending", "Auto", false, false, "")]
    [InlineData("Unknown", "Auto", false, false, "")]
    [InlineData("Running", "Auto", true, false, "")]            // Admin-list protected: no stop, no restart
    [InlineData("Paused", "Auto", true, false, "")]
    [InlineData("Stopped", "Auto", true, false, "start")]       // Admin-list protected: start is still fine
    [InlineData("Running", "Auto", true, true, "")]             // floor
    [InlineData("Stopped", "Auto", true, true, "start")]        // floor: starting a stopped critical service is remediation
    public void Allowed_actions_follow_state_start_mode_and_protection(string state, string mode, bool isProtected, bool floor, string expected) =>
        (floor && !isProtected ? throw new InvalidOperationException("a floor service is always protected") : ServiceRules.Allowed(state, mode, isProtected)).Should().Equal(expected.Split(',', StringSplitOptions.RemoveEmptyEntries));

    [Theory]
    [InlineData("Running", "Running")]
    [InlineData("running", "Running")]
    [InlineData("Start Pending", "StartPending")]
    [InlineData("Stop Pending", "StopPending")]
    [InlineData("Stopped", "Stopped")]
    [InlineData("Paused", "Paused")]
    [InlineData("whatever", "Unknown")]
    [InlineData(null, "Unknown")]
    public void WMI_states_are_normalised(string? raw, string expected) => ServiceRules.NormalizeState(raw).Should().Be(expected);

    [Fact]
    public void The_service_action_name_alias_applies_only_to_the_three_service_actions_and_only_without_serviceName()
    {
        static Dictionary<string, JsonElement> J(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

        ServiceRules.WithNameAlias("svc.stop", J("""{"name":"Spooler"}"""))!.Should().ContainKey("serviceName").And.NotContainKey("name");
        ServiceRules.WithNameAlias("svc.stop", J("""{"name":"A","serviceName":"B"}"""))!.Should().ContainKey("name", "both together are left for the validator to refuse");
        ServiceRules.WithNameAlias("proc.kill", J("""{"name":"x"}"""))!.Should().ContainKey("name");
        ServiceRules.WithNameAlias("svc.stop", null).Should().BeNull();
    }

    // ---------- risk tiers

    [Fact]
    public void Start_is_low_and_stop_and_restart_are_medium_in_the_catalog()
    {
        ActionCatalog.Find("svc.start")!.Risk.Should().Be(RiskLevel.Low);
        ActionCatalog.Find("svc.stop")!.Risk.Should().Be(RiskLevel.Medium);
        ActionCatalog.Find("svc.restart")!.Risk.Should().Be(RiskLevel.Medium);
    }

    [Theory]
    [InlineData("svc.stop")]
    [InlineData("svc.restart")]
    public async Task A_stop_or_restart_with_running_dependents_is_high_and_needs_the_typed_name(string code)
    {
        var r = new FakeReader { Dependents = _ => Deps(("FaxSvc", "Running"), ("Idle", "Stopped")) };

        var risk = await Escalator(r).EffectiveRiskAsync(ActionCatalog.Find(code)!, [Pc()], P(), CancellationToken.None);

        risk.Should().Be(RiskLevel.High);
        ActionDefinition.NeedsTypedConfirmationAt(risk).Should().BeTrue();
        ActionDefinition.NeedsReasonAt(risk).Should().BeTrue();
    }

    [Theory]
    [InlineData("svc.stop")]
    [InlineData("svc.restart")]
    public async Task Only_stopped_dependents_leave_the_risk_at_medium(string code)
    {
        var r = new FakeReader { Dependents = _ => Deps(("Idle", "Stopped")) };

        (await Escalator(r).EffectiveRiskAsync(ActionCatalog.Find(code)!, [Pc()], P(), CancellationToken.None)).Should().Be(RiskLevel.Medium);
    }

    [Fact]
    public async Task A_failed_dependents_lookup_fails_safe_to_high()
    {
        var r = new FakeReader { Dependents = _ => Failed };

        (await Escalator(r).EffectiveRiskAsync(ActionCatalog.Find("svc.stop")!, [Pc()], P(), CancellationToken.None)).Should().Be(RiskLevel.High);
    }

    [Fact]
    public async Task An_offline_target_is_high_without_any_lookup()
    {
        var r = new FakeReader();

        (await Escalator(r).EffectiveRiskAsync(ActionCatalog.Find("svc.restart")!, [Pc(online: false)], P(), CancellationToken.None)).Should().Be(RiskLevel.High);
        r.Calls.Should().Be(0);
    }

    [Fact]
    public async Task More_targets_than_the_lookup_limit_is_high_without_reading_them_all()
    {
        var r = new FakeReader();
        var many = Enumerable.Range(0, ServiceRiskEscalator.MaxLookupTargets + 1).Select(i => Pc("pc" + i)).ToList();

        (await Escalator(r).EffectiveRiskAsync(ActionCatalog.Find("svc.stop")!, many, P(), CancellationToken.None)).Should().Be(RiskLevel.High);
        r.Calls.Should().Be(0, "the number of reads a request can cause is bounded");
    }

    [Fact]
    public async Task One_bad_target_among_several_raises_the_whole_request()
    {
        var r = new FakeReader { Dependents = host => host == "pc2" ? Deps(("FaxSvc", "Running")) : Deps() };

        (await Escalator(r).EffectiveRiskAsync(ActionCatalog.Find("svc.stop")!, [Pc("pc1"), Pc("pc2")], P(), CancellationToken.None)).Should().Be(RiskLevel.High);
    }

    [Fact]
    public async Task Start_and_other_actions_are_never_looked_up_and_keep_their_catalog_risk()
    {
        var r = new FakeReader { Dependents = _ => Deps(("FaxSvc", "Running")) };
        var esc = Escalator(r);

        (await esc.EffectiveRiskAsync(ActionCatalog.Find("svc.start")!, [Pc()], P(), CancellationToken.None)).Should().Be(RiskLevel.Low);
        foreach (var def in ActionCatalog.All.Where(a => !ServiceRules.IsStopOrRestart(a.Code)))
            (await esc.EffectiveRiskAsync(def, [Pc()], P(), CancellationToken.None)).Should().Be(def.Risk, def.Code);
        r.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Without_a_service_name_the_catalog_risk_is_returned()
    {
        var r = new FakeReader { Dependents = _ => Deps(("FaxSvc", "Running")) };

        (await Escalator(r).EffectiveRiskAsync(ActionCatalog.Find("svc.stop")!, [Pc()], new Dictionary<string, string>(), CancellationToken.None)).Should().Be(RiskLevel.Medium);
    }

    // ---------- stop dependents guard

    [Fact]
    public async Task Stop_dependents_is_refused_when_a_dependent_is_protected_by_the_floor_or_the_admin_list()
    {
        var floor = new FakeReader { Dependents = _ => Deps(("FaxSvc", "Running"), ("WinDefend", "Running")) };
        var listed = new FakeReader { Dependents = _ => Deps(("FaxSvc", "Running"), ("MyCorpAgent", "Stopped")) };

        (await Guard(floor).DependentsRefusalAsync([Pc()], "Spooler")).Should().Contain("WinDefend").And.Contain("protected");
        (await Guard(listed, "MyCorpAgent").DependentsRefusalAsync([Pc()], "Spooler")).Should().Contain("MyCorpAgent");
        (await Guard(new FakeReader { Dependents = _ => Deps(("FaxSvc", "Running")) }).DependentsRefusalAsync([Pc()], "Spooler")).Should().BeNull();
    }

    [Fact]
    public async Task Stop_dependents_fails_safe_when_the_dependents_cannot_be_read_or_the_pc_is_offline_or_there_are_too_many_targets()
    {
        (await Guard(new FakeReader { Dependents = _ => Failed }).DependentsRefusalAsync([Pc()], "Spooler")).Should().Contain("could not be checked");
        (await Guard().DependentsRefusalAsync([Pc(online: false)], "Spooler")).Should().Contain("offline");
        var many = Enumerable.Range(0, ServiceGuard.MaxDependentTargets + 1).Select(i => Pc("pc" + i)).ToList();
        (await Guard().DependentsRefusalAsync(many, "Spooler")).Should().Contain("up to");
    }

    // ---------- the reader: bounded, deadline, typed failure

    private sealed class Clock : ISystemClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc);
    }

    private sealed class FakeCollector : IServiceCollector
    {
        public int Calls, Running, Peak;
        public Func<string, CancellationToken, Task<IReadOnlyList<ServiceInfo>>> OnServices { get; set; } = (_, _) => Task.FromResult<IReadOnlyList<ServiceInfo>>([new("Spooler", "Print Spooler", "Running", "Auto")]);

        public async Task<IReadOnlyList<ServiceInfo>> CollectServicesAsync(string host, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var now = Interlocked.Increment(ref Running);
            int peak;
            while ((peak = Volatile.Read(ref Peak)) < now && Interlocked.CompareExchange(ref Peak, now, peak) != peak) { }
            try { return await OnServices(host, ct); }
            finally { Interlocked.Decrement(ref Running); }
        }

        public Task<IReadOnlyList<DependentService>> CollectDependentsAsync(string host, string serviceName, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DependentService>>([new("FaxSvc", "Running")]);
    }

    private static ServiceReader Reader(FakeCollector c, Clock? clock = null, int timeoutSeconds = 2) =>
        new(c, Options.Create(new NexoraOptions { Monitoring = new MonitoringOptions { ServiceReadTimeoutSeconds = timeoutSeconds } }), clock ?? new Clock(), NullLogger<ServiceReader>.Instance);

    [Fact]
    public async Task At_most_four_reads_run_at_once_however_many_are_asked_for()
    {
        var release = new TaskCompletionSource();
        var c = new FakeCollector { OnServices = async (_, _) => { await release.Task; return []; } };
        var reader = Reader(c, timeoutSeconds: 30);

        var reads = Enumerable.Range(0, 10).Select(i => reader.ReadServicesAsync("pc" + i, TimeSpan.Zero, CancellationToken.None)).ToList();
        await Task.Delay(300);
        c.Peak.Should().Be(ServiceReader.MaxConcurrent);
        release.SetResult();
        var results = await Task.WhenAll(reads);

        results.Should().OnlyContain(r => r.Ok);
        c.Peak.Should().Be(ServiceReader.MaxConcurrent);
    }

    [Fact]
    public async Task A_read_that_throws_is_a_typed_result_with_a_generic_reason_never_the_exception_text()
    {
        var c = new FakeCollector { OnServices = (_, _) => throw new InvalidOperationException("RPC server 10.99.99.99 \\\\internal-share secret") };

        var r = await Reader(c).ReadServicesAsync("pc1", TimeSpan.Zero, CancellationToken.None);

        r.Ok.Should().BeFalse();
        r.Reason.Should().Be(ServiceReader.ReadFailed).And.NotContain("10.99");
    }

    [Fact]
    public async Task A_read_that_never_returns_ends_at_the_deadline_as_a_typed_failure()
    {
        var c = new FakeCollector { OnServices = (_, _) => new TaskCompletionSource<IReadOnlyList<ServiceInfo>>().Task };
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var r = await Reader(c, timeoutSeconds: 2).ReadServicesAsync("pc1", TimeSpan.Zero, CancellationToken.None);

        r.Ok.Should().BeFalse();
        r.Reason.Should().Be(ServiceReader.ReadFailed);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(6));
    }

    [Fact]
    public async Task A_cancelled_caller_gets_the_cancellation_not_a_result()
    {
        var c = new FakeCollector { OnServices = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return []; } };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = () => Reader(c, timeoutSeconds: 30).ReadServicesAsync("pc1", TimeSpan.Zero, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_fresh_result_is_served_from_the_cache_and_a_failed_host_is_not_hammered()
    {
        var clock = new Clock();
        var c = new FakeCollector();
        var reader = Reader(c, clock);

        await reader.ReadServicesAsync("pc1", TimeSpan.FromSeconds(15), CancellationToken.None);
        await reader.ReadServicesAsync("pc1", TimeSpan.FromSeconds(15), CancellationToken.None);
        c.Calls.Should().Be(1);

        clock.UtcNow = clock.UtcNow.AddSeconds(20);
        await reader.ReadServicesAsync("pc1", TimeSpan.FromSeconds(15), CancellationToken.None);
        c.Calls.Should().Be(2, "older than the allowed age");

        var failing = new FakeCollector { OnServices = (_, _) => throw new InvalidOperationException("down") };
        var r2 = Reader(failing, clock);
        await r2.ReadServicesAsync("dead", TimeSpan.Zero, CancellationToken.None);
        await r2.ReadServicesAsync("dead", TimeSpan.Zero, CancellationToken.None);
        failing.Calls.Should().Be(1, "a failure is remembered for a few seconds");
    }

    [Fact]
    public async Task The_last_successful_read_is_kept_for_an_offline_view()
    {
        var clock = new Clock();
        var c = new FakeCollector();
        var reader = Reader(c, clock);
        reader.LastKnownServices("pc1").Should().BeNull();

        await reader.ReadServicesAsync("pc1", TimeSpan.Zero, CancellationToken.None);
        c.OnServices = (_, _) => throw new InvalidOperationException("down");
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        (await reader.ReadServicesAsync("pc1", TimeSpan.Zero, CancellationToken.None)).Ok.Should().BeFalse();

        reader.LastKnownServices("pc1")!.Value.Services.Should().ContainSingle().Which.Name.Should().Be("Spooler");
    }

    [Fact]
    public async Task An_invalid_service_name_is_never_passed_to_the_collector()
    {
        var c = new FakeCollector();

        var r = await Reader(c).ReadDependentsAsync("pc1", "a'; DROP", TimeSpan.Zero, CancellationToken.None);

        r.Ok.Should().BeFalse();
    }

    [Theory]
    [InlineData("Spooler", true)]
    [InlineData("Print Spooler", true)]
    [InlineData("svc$1", true)]
    [InlineData("Spooler\n", false)]
    [InlineData("a'b", false)]
    [InlineData("a\\b", false)]
    [InlineData("", false)]
    public void Service_names_are_validated_with_an_end_anchor(string name, bool ok) => ServiceNameRules.IsValid(name).Should().Be(ok);

    // ---------- text

    [Fact]
    public void No_catalog_text_uses_the_old_process_verbs()
    {
        foreach (var a in ActionCatalog.All.Where(a => a.Category is "processes" or "services"))
        {
            (a.DisplayName + " " + a.Description).Should().NotContainEquivalentOf("terminate", a.Code).And.NotContainEquivalentOf("end process", a.Code);
            foreach (var p in a.Parameters) (p.Label + " " + p.Help).Should().NotContainEquivalentOf("terminate");
        }
    }

    [Fact]
    public void Start_has_no_deny_list_but_stop_and_restart_still_deny_the_floor()
    {
        static Dictionary<string, JsonElement> J(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

        ParameterValidator.Validate(ActionCatalog.Find("svc.start")!, J("""{"serviceName":"Netlogon"}""")).Error.Should().BeNull();
        ParameterValidator.Validate(ActionCatalog.Find("svc.stop")!, J("""{"serviceName":"Netlogon"}""")).Error.Should().Contain("protected");
        ParameterValidator.Validate(ActionCatalog.Find("svc.restart")!, J("""{"serviceName":"Netlogon"}""")).Error.Should().Contain("protected");
    }

    [Fact]
    public async Task Identical_concurrent_reads_share_one_collector_call()
    {
        var release = new TaskCompletionSource();
        var c = new FakeCollector { OnServices = async (_, _) => { await release.Task; return []; } };
        var reader = Reader(c, timeoutSeconds: 30);

        var reads = Enumerable.Range(0, 6).Select(_ => reader.ReadServicesAsync("pc1", TimeSpan.Zero, CancellationToken.None)).ToList();
        await Task.Delay(200);
        release.SetResult();
        (await Task.WhenAll(reads)).Should().OnlyContain(r => r.Ok);

        c.Calls.Should().Be(1);
    }

    private sealed class SlowReader(TimeSpan delay) : IServiceReader
    {
        public Task<ServiceRead<IReadOnlyList<ServiceInfo>>> ReadServicesAsync(string host, TimeSpan maxAge, CancellationToken ct) => throw new NotSupportedException();
        public async Task<ServiceRead<IReadOnlyList<DependentService>>> ReadDependentsAsync(string host, string serviceName, TimeSpan maxAge, CancellationToken ct)
        {
            await Task.Delay(delay, ct);
            return new([], null);
        }
        public (IReadOnlyList<ServiceInfo> Services, DateTime AtUtc)? LastKnownServices(string host) => null;
    }

    [Fact]
    public async Task Several_targets_are_checked_in_parallel_not_one_after_the_other()
    {
        var targets = Enumerable.Range(0, 5).Select(i => Pc("pc" + i)).ToList();
        var guard = new ServiceGuard(new FakeSettings(new PlatformSettings()), new SlowReader(TimeSpan.FromMilliseconds(400)));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        (await guard.DependentsRefusalAsync(targets, "Spooler")).Should().BeNull();

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(1500), "5 x 400 ms in sequence would be 2 s");
    }

    [Fact]
    public async Task Start_on_several_workstations_is_medium_and_on_one_it_stays_low()
    {
        var esc = Escalator(new FakeReader());
        var start = ActionCatalog.Find("svc.start")!;

        (await esc.EffectiveRiskAsync(start, [Pc("a")], P(), CancellationToken.None)).Should().Be(RiskLevel.Low);
        (await esc.EffectiveRiskAsync(start, [Pc("a"), Pc("b")], P(), CancellationToken.None)).Should().Be(RiskLevel.Medium);
        ActionDefinition.NeedsConfirmationAt(RiskLevel.Medium).Should().BeTrue();
    }

    [Theory]
    [InlineData("WinRM")]
    [InlineData("winrm")]
    [InlineData("RemoteRegistry")]
    [InlineData("sshd")]
    [InlineData("TlntSvr")]
    public void Remote_management_services_cannot_be_started_but_can_be_stopped(string name)
    {
        static Dictionary<string, JsonElement> J(string n) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>($"{{\"serviceName\":\"{n}\"}}")!;

        ParameterValidator.Validate(ActionCatalog.Find("svc.start")!, J(name)).Error.Should().Contain("protected");
        ParameterValidator.Validate(ActionCatalog.Find("svc.restart")!, J(name)).Error.Should().BeNull("restart is guarded by the helper and the floor, not by the start list");
        ParameterValidator.Validate(ActionCatalog.Find("svc.stop")!, J(name)).Error.Should().BeNull();
    }
}
