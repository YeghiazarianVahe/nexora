using System.Text.RegularExpressions;
using FluentAssertions;
using Nexora.Contracts;
using Nexora.Helper.Actions;

namespace Nexora.Application.Tests;

/// <summary>svc.stop / svc.restart with stopDependents, against a fake service graph (no WMI, no PC).</summary>
public sealed partial class HelperServiceDependentsTests
{
    [GeneratedRegex(@"ASSOCIATORS OF \{Win32_Service\.Name='([^']+)'\}")]
    private static partial Regex AssocPattern();

    [GeneratedRegex(@"Name = '([^']+)'")]
    private static partial Regex NamePattern();

    private sealed class Graph : IWmiHost
    {
        public Dictionary<string, string> State { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string[]> DependentsOf { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<(string, string), int> Rc { get; } = [];
        public HashSet<string> NeverStarts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> NeverStops { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Calls { get; } = [];

        private WmiRow Row(string n) => new(n, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Name"] = n, ["State"] = State[n] });

        public IReadOnlyList<WmiRow> Query(string wql)
        {
            if (AssocPattern().Match(wql) is { Success: true } a)
                return DependentsOf.GetValueOrDefault(a.Groups[1].Value, []).Select(Row).ToList();
            var n = NamePattern().Match(wql).Groups[1].Value;
            return State.ContainsKey(n) ? [Row(n)] : [];
        }

        public int Invoke(WmiRow row, string method)
        {
            var n = (string)row.Handle;
            Calls.Add($"{method}:{n}");
            if (Rc.TryGetValue((n, method), out var rc)) return rc;
            if (method == "StopService" && !NeverStops.Contains(n)) State[n] = "Stopped";
            if (method == "StartService" && !NeverStarts.Contains(n)) State[n] = "Running";
            return 0;
        }

        public void Delete(WmiRow row) { }
        public void Dispose() { }
    }

    private sealed class Sink : IJobSink
    {
        public List<string> Lines { get; } = [];
        public Task Log(string stream, string line) { Lines.Add(line); return Task.CompletedTask; }
        public Task Progress(int percent) => Task.CompletedTask;
    }

    // Spooler <- FaxSvc <- FaxHelper (FaxHelper depends on FaxSvc, FaxSvc depends on Spooler); Idle depends on Spooler but is stopped.
    private static Graph Fleet()
    {
        var g = new Graph();
        g.State["Spooler"] = "Running"; g.State["FaxSvc"] = "Running"; g.State["FaxHelper"] = "Running"; g.State["Idle"] = "Stopped";
        g.DependentsOf["Spooler"] = ["FaxSvc", "Idle"];
        g.DependentsOf["FaxSvc"] = ["FaxHelper"];
        return g;
    }

    private static JobPayload Job(string code, bool? stopDependents, string service = "Spooler", int timeout = 60)
    {
        var p = new Dictionary<string, string> { ["serviceName"] = service };
        if (stopDependents is { } b) p["stopDependents"] = b ? "true" : "false";
        return new(Guid.NewGuid(), Guid.NewGuid(), code, "PC-1", "S-1-5-21-1-2-3-1000", p, timeout, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(2), "n");
    }

    private static ServiceAction Action(Graph g, TimeSpan? wait = null) => new(_ => g, wait ?? TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(5));

    [Fact]
    public async Task Paused_dependents_are_stopped_before_the_parent()
    {
        var g = Fleet();
        g.State["FaxSvc"] = "Paused";
        var outcome = await Action(g).RunAsync(Job("svc.stop", true), new Sink(), CancellationToken.None);
        outcome.State.Should().Be("Succeeded");
        g.Calls.Should().Equal("StopService:FaxHelper", "StopService:FaxSvc", "StopService:Spooler");
    }

    [Fact]
    public async Task A_protected_dependent_refuses_the_whole_operation_before_anything_is_stopped()
    {
        var g = Fleet();
        g.State["WinDefend"] = "Running";
        g.DependentsOf["FaxHelper"] = ["WinDefend"];

        var outcome = await Action(g).RunAsync(Job("svc.stop", true), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Failed");
        outcome.Summary.Should().Contain("WinDefend").And.Contain("nothing was stopped");
        g.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_stopDependents_a_stop_that_returns_3_names_the_running_dependents_and_stops_none()
    {
        var g = Fleet();
        g.Rc[("Spooler", "StopService")] = 3;

        var outcome = await Action(g).RunAsync(Job("svc.stop", false), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Failed");
        outcome.ExitCode.Should().Be(3);
        outcome.Summary.Should().Contain("FaxSvc").And.Contain("FaxHelper").And.NotContain("Idle");
        g.Calls.Should().Equal("StopService:Spooler");
    }

    [Fact]
    public async Task Restart_with_dependents_stops_leaves_first_then_restarts_only_those_that_were_running()
    {
        var g = Fleet();
        var sink = new Sink();

        var outcome = await Action(g).RunAsync(Job("svc.restart", true), sink, CancellationToken.None);

        outcome.State.Should().Be("Succeeded");
        g.Calls.Should().Equal("StopService:FaxHelper", "StopService:FaxSvc", "StopService:Spooler",
            "StartService:Spooler", "StartService:FaxSvc", "StartService:FaxHelper");
        g.Calls.Should().NotContain("StartService:Idle").And.NotContain("StopService:Idle");
        sink.Lines.Should().Contain(l => l.Contains("Restarting dependent FaxSvc"));
        g.State.Should().Contain(new KeyValuePair<string, string>("Idle", "Stopped"));
    }

    [Fact]
    public async Task A_dependent_that_does_not_come_back_makes_the_restart_Failed_with_details()
    {
        var g = Fleet();
        g.NeverStarts.Add("FaxHelper");

        var outcome = await Action(g, TimeSpan.FromMilliseconds(100)).RunAsync(Job("svc.restart", true), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Failed");
        outcome.Summary.Should().Contain("Partial").And.Contain("FaxHelper").And.Contain("Spooler");
        g.State["Spooler"].Should().Be("Running");
    }

    [Fact]
    public async Task A_dependent_that_will_not_stop_leaves_the_service_running_and_fails()
    {
        var g = Fleet();
        g.NeverStops.Add("FaxSvc");

        var outcome = await Action(g, TimeSpan.FromMilliseconds(100)).RunAsync(Job("svc.stop", true), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Failed");
        outcome.Summary.Should().Contain("FaxSvc").And.Contain("timed out");
        g.Calls.Should().NotContain("StopService:Spooler");
    }

    [Fact]
    public async Task Waiting_for_Stopped_times_out_as_Failed()
    {
        var g = Fleet();
        g.NeverStops.Add("Spooler");

        var outcome = await Action(g, TimeSpan.FromMilliseconds(100)).RunAsync(Job("svc.stop", true), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Failed");
        outcome.Summary.Should().Contain("Timed out");
    }

    [Fact]
    public async Task Cancellation_while_waiting_is_honoured()
    {
        var g = Fleet();
        g.NeverStops.Add("Spooler");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = () => Action(g, TimeSpan.FromSeconds(30)).RunAsync(Job("svc.stop", true), new Sink(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task The_default_wait_is_60_seconds_and_a_stop_without_the_flag_and_without_dependents_still_works()
    {
        var g = new Graph();
        g.State["Lonely"] = "Running";

        var outcome = await new ServiceAction(_ => g).RunAsync(Job("svc.stop", null, "Lonely"), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Succeeded");
        g.Calls.Should().Equal("StopService:Lonely");
    }

    [Fact]
    public async Task Stop_returning_3_after_dependents_were_stopped_reports_them_and_starts_them_again()
    {
        var g = Fleet();
        g.Rc[("Spooler", "StopService")] = 3;

        var outcome = await Action(g).RunAsync(Job("svc.restart", true), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Failed");
        outcome.ExitCode.Should().Be(3);
        outcome.Summary.Should().Contain("Dependents already stopped: FaxHelper, FaxSvc").And.Contain("started again");
        g.Calls.Should().Equal("StopService:FaxHelper", "StopService:FaxSvc", "StopService:Spooler", "StartService:FaxSvc", "StartService:FaxHelper");
        g.State["FaxSvc"].Should().Be("Running");
        g.State["FaxHelper"].Should().Be("Running");
    }

    [Fact]
    public async Task Cancellation_while_stopping_logs_what_was_left_stopped()
    {
        var g = Fleet();
        g.NeverStops.Add("Spooler");
        var sink = new Sink();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var act = () => Action(g, TimeSpan.FromSeconds(30)).RunAsync(Job("svc.stop", true), sink, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        sink.Lines.Should().Contain(l => l.StartsWith("Cancelled; left stopped:") && l.Contains("FaxHelper") && l.Contains("FaxSvc") && l.Contains("Spooler"));
    }

    [Fact]
    public async Task Cancellation_while_restarting_logs_what_was_left_stopped()
    {
        var g = Fleet();
        g.NeverStarts.Add("Spooler");
        var sink = new Sink();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var act = () => Action(g, TimeSpan.FromSeconds(30)).RunAsync(Job("svc.restart", true), sink, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        sink.Lines.Should().Contain(l => l.StartsWith("Cancelled; left stopped:") && l.Contains("FaxHelper") && l.Contains("FaxSvc"));
    }

    [Fact]
    public async Task Waits_are_limited_by_the_job_budget_so_the_job_fails_instead_of_being_cut_off()
    {
        var g = Fleet();
        g.NeverStops.Add("Spooler");
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var outcome = await Action(g, TimeSpan.FromSeconds(60)).RunAsync(Job("svc.stop", true, timeout: 3), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Failed");
        outcome.Summary.Should().Contain("Timed out");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_dependency_tree_deeper_than_16_is_refused_not_truncated()
    {
        var g = new Graph();
        for (var i = 0; i < 20; i++) g.State[$"S{i}"] = "Running";
        for (var i = 0; i < 19; i++) g.DependentsOf[$"S{i}"] = [$"S{i + 1}"];

        var outcome = await Action(g).RunAsync(Job("svc.stop", true, "S0"), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Failed");
        outcome.Summary.Should().Contain("too deep").And.Contain("nothing was stopped");
        g.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_dependent_that_answers_5_was_not_running_so_it_is_not_started_again()
    {
        var g = Fleet();
        g.Rc[("FaxHelper", "StopService")] = 5;

        var outcome = await Action(g).RunAsync(Job("svc.restart", true), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Succeeded");
        g.Calls.Should().NotContain("StartService:FaxHelper");
        g.Calls.Should().Contain("StartService:FaxSvc");
    }

    [Theory]
    [InlineData("windefend")]
    [InlineData("WinDefend ")]
    [InlineData(" RpcSs")]
    public async Task Protected_names_are_matched_ignoring_case_and_surrounding_spaces(string name)
    {
        var g = new Graph();
        g.State[name] = "Running";

        var outcome = await Action(g).RunAsync(Job("svc.stop", true, name), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Failed");
        outcome.Summary.Should().Contain("protected");
        g.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_dependent_with_a_padded_protected_name_refuses_before_stopping_anything()
    {
        var g = Fleet();
        g.State["BFE "] = "Running";
        g.DependentsOf["FaxHelper"] = ["BFE "];

        var outcome = await Action(g).RunAsync(Job("svc.stop", true), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Failed");
        g.Calls.Should().BeEmpty();
    }

    // ---------- F2 carry-overs (S0c review)

    [Fact]
    public async Task A_timed_out_dependent_stop_after_others_were_stopped_says_they_were_not_started_again()
    {
        var g = Fleet();
        g.NeverStops.Add("FaxSvc"); // FaxHelper (a leaf) stops first, then FaxSvc times out

        var outcome = await Action(g, TimeSpan.FromMilliseconds(100)).RunAsync(Job("svc.restart", true), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Failed");
        outcome.Summary.Should().Contain("Dependents already stopped: FaxHelper").And.Contain("They were NOT started again.");
        g.Calls.Should().NotContain(c => c.StartsWith("StartService:", StringComparison.Ordinal), "out of time: nothing is started again");
        g.Calls.Should().NotContain("StopService:Spooler");
    }

    [Fact]
    public async Task A_timed_out_dependent_stop_with_nothing_stopped_before_it_makes_no_such_claim()
    {
        var g = Fleet();
        g.NeverStops.Add("FaxHelper"); // the first leaf

        var outcome = await Action(g, TimeSpan.FromMilliseconds(100)).RunAsync(Job("svc.stop", true), new Sink(), CancellationToken.None);

        outcome.Summary.Should().Contain("timed out").And.NotContain("NOT started again");
    }

    [Fact]
    public async Task The_original_failure_is_logged_before_the_rollback_starts_dependents_again()
    {
        var g = Fleet();
        g.Rc[("Spooler", "StopService")] = 3;
        var sink = new Sink();

        var outcome = await Action(g).RunAsync(Job("svc.restart", true), sink, CancellationToken.None);

        outcome.State.Should().Be("Failed");
        var failure = sink.Lines.FindIndex(l => l.Contains("Dependents already stopped: FaxHelper, FaxSvc"));
        var rollback = sink.Lines.FindIndex(l => l.StartsWith("Starting dependent", StringComparison.Ordinal));
        failure.Should().BeGreaterThanOrEqualTo(0, "the failure summary is logged");
        rollback.Should().BeGreaterThan(failure, "and it comes before the first dependent is started again");
        sink.Lines[failure].Should().NotContain("started again", "it is the failure as it was, before the rollback result is appended");
    }

    [Fact]
    public async Task A_restart_keeps_half_of_the_time_budget_for_the_start_side()
    {
        var g = Fleet();
        g.NeverStops.Add("Spooler");
        var watch = System.Diagnostics.Stopwatch.StartNew();

        // Budget = 10 - 3 = 7 s. The stop phase may take at most half (3.5 s); an unrestricted wait would have used all of it.
        var outcome = await Action(g, TimeSpan.FromSeconds(60)).RunAsync(Job("svc.restart", true, timeout: 10), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Failed");
        outcome.Summary.Should().Contain("Timed out");
        watch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(3)).And.BeLessThan(TimeSpan.FromSeconds(5.5));
    }

    [Fact]
    public async Task After_the_stop_phase_the_start_side_is_not_limited_by_the_stop_phase_deadline()
    {
        var g = Fleet();

        var outcome = await Action(g, TimeSpan.FromSeconds(2)).RunAsync(Job("svc.restart", true, timeout: 10), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Succeeded");
        g.State["Spooler"].Should().Be("Running");
        g.State["FaxHelper"].Should().Be("Running");
    }

    [Fact]
    public async Task Starting_a_stopped_floor_service_is_allowed_but_stop_and_restart_of_it_are_refused()
    {
        var g = new Graph();
        g.State["Netlogon"] = "Stopped";

        var start = await Action(g).RunAsync(Job("svc.start", null, "Netlogon"), new Sink(), CancellationToken.None);
        start.State.Should().Be("Succeeded");
        g.Calls.Should().Equal("StartService:Netlogon");

        g.Calls.Clear();
        foreach (var code in new[] { "svc.stop", "svc.restart" })
        {
            var refused = await Action(g).RunAsync(Job(code, null, "Netlogon"), new Sink(), CancellationToken.None);
            refused.State.Should().Be("Failed");
            refused.Summary.Should().Contain("protected");
        }
        g.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("svc.start")]
    [InlineData("svc.restart")]
    public async Task The_helper_never_starts_remote_management_services(string code)
    {
        var g = new Graph();
        foreach (var n in ProtectedNames.NoStartServices) g.State[n] = "Stopped";

        foreach (var n in ProtectedNames.NoStartServices)
        {
            var outcome = await Action(g).RunAsync(Job(code, null, n), new Sink(), CancellationToken.None);
            outcome.State.Should().Be("Failed", n);
            outcome.Summary.Should().Contain("never starts");
        }
        g.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Stopping_a_remote_management_service_is_not_blocked_by_the_start_list()
    {
        var g = new Graph();
        g.State["WinRM"] = "Running";

        (await Action(g).RunAsync(Job("svc.stop", null, "WinRM"), new Sink(), CancellationToken.None)).State.Should().Be("Succeeded");
    }

    [Fact]
    public async Task A_floor_service_that_is_a_dependent_of_a_restart_refuses_it_before_anything_is_stopped()
    {
        var g = Fleet();
        g.State["Netlogon"] = "Running";
        g.DependentsOf["FaxSvc"] = ["FaxHelper", "Netlogon"];

        var outcome = await Action(g).RunAsync(Job("svc.restart", true), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Failed");
        outcome.Summary.Should().Contain("Netlogon").And.Contain("nothing was stopped");
        g.Calls.Should().BeEmpty();
    }
}
