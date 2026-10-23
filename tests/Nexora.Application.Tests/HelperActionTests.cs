using FluentAssertions;
using Nexora.Contracts;
using Nexora.Helper.Actions;

namespace Nexora.Application.Tests;

/// <summary>The helper's real actions against a fake WMI layer: what they call, and what they refuse before calling anything.</summary>
public sealed class HelperActionTests
{
    private sealed class FakeWmi : IWmiHost
    {
        public List<string> Queries { get; } = [];
        public List<(string Class, string Method)> Calls { get; } = [];
        public List<WmiRow> Deleted { get; } = [];
        public Dictionary<string, List<WmiRow>> Rows { get; } = [];
        public Func<WmiRow, string, int> OnInvoke { get; set; } = (_, _) => 0;
        public bool ExitOnTerminate { get; set; } = true;
        public bool Disposed { get; private set; }
        /// <summary>A query that contains this text throws, as WMI does when a PC cannot answer.</summary>
        public string? ThrowWhenQueryContains { get; set; }

        public IReadOnlyList<WmiRow> Query(string wql)
        {
            Queries.Add(wql);
            if (ThrowWhenQueryContains is { } boom && wql.Contains(boom, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("WMI unavailable");
            var table = Rows.Keys.FirstOrDefault(k => wql.Contains(k, StringComparison.OrdinalIgnoreCase));
            return table is null ? [] : Rows[table];
        }

        public int Invoke(WmiRow row, string method)
        {
            Calls.Add((row.Text("__Class") ?? "?", method));
            var rc = OnInvoke(row, method);
            if (method == "Terminate" && rc == 0 && ExitOnTerminate) Rows["Win32_Process"] = [];
            return rc;
        }

        public void Delete(WmiRow row) => Deleted.Add(row);
        public void Dispose() => Disposed = true;
    }

    private sealed class Sink : IJobSink
    {
        public List<string> Lines { get; } = [];
        public Task Log(string stream, string line) { Lines.Add(line); return Task.CompletedTask; }
        public Task Progress(int percent) => Task.CompletedTask;
    }

    private static WmiRow Row(string cls, params (string, object?)[] props) =>
        new(new object(), new Dictionary<string, object?>(props.Select(p => new KeyValuePair<string, object?>(p.Item1, p.Item2)).Append(new("__Class", cls)), StringComparer.OrdinalIgnoreCase));

    private static JobPayload Job(string code, string host = "PC-1", string initiator = "S-1-5-21-1-2-3-1000", params (string, string)[] p) =>
        new(Guid.NewGuid(), Guid.NewGuid(), code, host, initiator, p.ToDictionary(x => x.Item1, x => x.Item2), 60, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(2), "n");

    // ---------- services

    [Fact]
    public async Task Restart_stops_waits_and_starts_through_Win32_Service()
    {
        var wmi = new FakeWmi();
        var state = "Running";
        wmi.Rows["Win32_Service"] = [];
        wmi.OnInvoke = (_, method) => { state = method == "StopService" ? "Stopped" : "Running"; return 0; };
        // The row's State must follow the service: rebuild it on every query.
        var host = new FollowingHost(wmi, () => Row("Win32_Service", ("Name", "Spooler"), ("State", state)));

        var outcome = await new ServiceAction(_ => host, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(5))
            .RunAsync(Job("svc.restart", p: ("serviceName", "Spooler")), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Succeeded");
        wmi.Calls.Select(c => c.Method).Should().Equal("StopService", "StartService");
        wmi.Queries.Should().OnlyContain(q => q.Contains("Name = 'Spooler'"));
    }

    private sealed class FollowingHost(FakeWmi inner, Func<WmiRow> current) : IWmiHost
    {
        public IReadOnlyList<WmiRow> Query(string wql) { inner.Queries.Add(wql); return [current()]; }
        public int Invoke(WmiRow row, string method) => inner.Invoke(row, method);
        public void Delete(WmiRow row) => inner.Delete(row);
        public void Dispose() { }
    }

    [Theory]
    [InlineData("svc.stop", "Stopped", "StopService")]
    [InlineData("svc.start", "Running", "StartService")]
    public async Task Start_and_stop_call_only_their_own_method(string code, string finalState, string method)
    {
        var wmi = new FakeWmi();
        var state = code == "svc.stop" ? "Running" : "Stopped";
        wmi.OnInvoke = (_, _) => { state = finalState; return 0; };
        var host = new FollowingHost(wmi, () => Row("Win32_Service", ("Name", "Spooler"), ("State", state)));

        var outcome = await new ServiceAction(_ => host, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(5))
            .RunAsync(Job(code, p: ("serviceName", "Spooler")), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Succeeded");
        wmi.Calls.Select(c => c.Method).Should().Equal(method);
    }

    [Theory]
    [InlineData("WinDefend")]
    [InlineData("rpcss")]
    [InlineData("x'; drop")]
    [InlineData("")]
    public async Task Protected_or_malformed_service_names_are_refused_before_any_wmi_call(string name)
    {
        var wmi = new FakeWmi();
        var outcome = await new ServiceAction(_ => wmi).RunAsync(Job("svc.stop", p: ("serviceName", name)), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Failed");
        wmi.Queries.Should().BeEmpty();
        wmi.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_service_that_is_already_in_the_wanted_state_is_left_alone()
    {
        var wmi = new FakeWmi();
        wmi.Rows["Win32_Service"] = [Row("Win32_Service", ("Name", "Spooler"), ("State", "Running"))];

        var outcome = await new ServiceAction(_ => wmi).RunAsync(Job("svc.start", p: ("serviceName", "Spooler")), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Succeeded");
        outcome.Summary.Should().Contain("already running");
        wmi.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_missing_service_fails_cleanly()
    {
        var outcome = await new ServiceAction(_ => new FakeWmi()).RunAsync(Job("svc.restart", p: ("serviceName", "Nope")), new Sink(), CancellationToken.None);
        outcome.State.Should().Be("Failed");
        outcome.Summary.Should().Contain("not found");
    }

    [Theory]
    [InlineData("Paused")]
    [InlineData("Start Pending")]
    [InlineData("Stop Pending")]
    public async Task Stop_does_not_mistake_an_active_service_for_stopped(string initialState)
    {
        var wmi = new FakeWmi();
        var state = initialState;
        wmi.OnInvoke = (_, _) => { state = "Stopped"; return 0; };
        var host = new FollowingHost(wmi, () => Row("Win32_Service", ("Name", "Spooler"), ("State", state)));
        var result = await new ServiceAction(_ => host, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(5))
            .RunAsync(Job("svc.stop", p: ("serviceName", "Spooler")), new Sink(), CancellationToken.None);
        result.State.Should().Be("Succeeded");
        wmi.Calls.Should().ContainSingle().Which.Method.Should().Be("StopService");
    }

    [Fact]
    public async Task Unknown_service_state_cannot_be_reported_as_already_stopped()
    {
        var wmi = new FakeWmi();
        wmi.Rows["Win32_Service"] = [Row("Win32_Service", ("Name", "Spooler"), ("State", "Unknown"))];
        var result = await new ServiceAction(_ => wmi).RunAsync(Job("svc.stop", p: ("serviceName", "Spooler")), new Sink(), CancellationToken.None);
        result.State.Should().Be("Failed");
        wmi.Calls.Should().BeEmpty();
    }

    // ---------- processes

    [Fact]
    public async Task Kill_matches_pid_and_name_and_terminates()
    {
        var wmi = new FakeWmi();
        wmi.Rows["Win32_Process"] = [Row("Win32_Process", ("ProcessId", 4242), ("Name", "notepad.exe"))];

        var outcome = await new KillProcessAction(_ => wmi).RunAsync(Job("proc.kill", p: [("pid", "4242"), ("processName", "notepad.exe")]), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Succeeded");
        wmi.Queries.Should().HaveCount(2);
        wmi.Queries[0].Should().Contain("ProcessId = 4242").And.Contain("Name = 'notepad.exe'");
        wmi.Calls.Should().Equal(("Win32_Process", "Terminate"));
    }

    [Fact]
    public async Task Stopping_a_process_reports_Stopped_and_never_uses_the_old_verbs()
    {
        var wmi = new FakeWmi();
        wmi.Rows["Win32_Process"] = [Row("Win32_Process", ("ProcessId", 4242), ("Name", "notepad.exe"))];
        var ok = await new KillProcessAction(_ => wmi).RunAsync(Job("proc.kill", p: [("pid", "4242"), ("processName", "notepad.exe")]), new Sink(), CancellationToken.None);
        wmi.Rows["Win32_Process"] = [Row("Win32_Process", ("ProcessId", 4242), ("Name", "notepad.exe"))];
        wmi.OnInvoke = (_, _) => 2;
        var failed = await new KillProcessAction(_ => wmi).RunAsync(Job("proc.kill", p: [("pid", "4242"), ("processName", "notepad.exe")]), new Sink(), CancellationToken.None);

        ok.Summary.Should().StartWith("Stopped notepad.exe");
        failed.State.Should().Be("Failed");
        failed.Summary.Should().Contain("return code 2");
        (ok.Summary + failed.Summary).Should().NotContainEquivalentOf("terminat");
    }

    [Theory]
    [InlineData("4", "notepad.exe")]
    [InlineData("abc", "notepad.exe")]
    [InlineData("4242", "lsass.exe")]
    [InlineData("4242", "System")]
    [InlineData("4242", "a;b")]
    public async Task System_processes_and_bad_ids_are_refused_before_any_wmi_call(string pid, string name)
    {
        var wmi = new FakeWmi();
        var outcome = await new KillProcessAction(_ => wmi).RunAsync(Job("proc.kill", p: [("pid", pid), ("processName", name)]), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Failed");
        wmi.Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task Accepted_stop_that_does_not_exit_is_a_failure()
    {
        var wmi = new FakeWmi { ExitOnTerminate = false };
        wmi.Rows["Win32_Process"] = [Row("Win32_Process", ("ProcessId", 4242), ("Name", "notepad.exe"))];
        var result = await new KillProcessAction(_ => wmi, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(5))
            .RunAsync(Job("proc.kill", p: [("pid", "4242"), ("processName", "notepad.exe")]), new Sink(), CancellationToken.None);
        result.State.Should().Be("Failed");
        result.Summary.Should().Contain("exit could not be verified");
        wmi.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task Cancellation_after_lookup_prevents_stop()
    {
        using var cts = new CancellationTokenSource();
        var wmi = new FakeWmi();
        var host = new FollowingHost(wmi, () => { cts.Cancel(); return Row("Win32_Process", ("Name", "notepad.exe")); });
        var act = () => new KillProcessAction(_ => host).RunAsync(Job("proc.kill", p: [("pid", "4242"), ("processName", "notepad.exe")]), new Sink(), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        wmi.Calls.Should().BeEmpty();
    }

    // ---------- profiles

    private const string TargetSid = "S-1-5-21-1-2-3-4001";

    private static FakeWmi ProfileHost(bool loaded = false, bool special = false, string path = @"C:\Users\old.user")
    {
        var wmi = new FakeWmi();
        wmi.Rows["Win32_UserProfile"] = [Row("Win32_UserProfile", ("SID", TargetSid), ("LocalPath", path), ("Loaded", loaded), ("Special", special))];
        return wmi;
    }

    /// <summary>Puts an interactive-type logon session for <paramref name="sid"/> on the fake PC. The association query must be found before the session query.</summary>
    private static void SignedIn(FakeWmi wmi, string sid, int logonType = 2)
    {
        wmi.Rows["ASSOCIATORS OF"] = [Row("Win32_UserAccount", ("SID", sid), ("Name", "someone"))];
        wmi.Rows["Win32_LogonSession"] = [Row("Win32_LogonSession", ("LogonId", "999"), ("LogonType", logonType))];
    }

    private static JobPayload DeleteJob(string sid = TargetSid, string path = @"C:\Users\old.user", string initiator = "S-1-5-21-1-2-3-1000", string? size = null) =>
        Job("profile.delete", initiator: initiator, p: size is null ? [("sid", sid), ("localPath", path)] : [("sid", sid), ("localPath", path), ("sizeBytes", size)]);

    /// <summary>The folder attribute check is always faked in tests: the default one would reach for \\PC-1\C$ on the network.</summary>
    private static DeleteProfileAction Action(IWmiHost wmi, FileAttributes? folder = FileAttributes.Directory, List<string>? asked = null) =>
        new(_ => wmi, p => { asked?.Add(p); return folder; });

    [Fact]
    public async Task Profile_delete_calls_Win32_UserProfile_Delete_on_the_matching_profile()
    {
        var wmi = ProfileHost();
        var asked = new List<string>();

        var outcome = await Action(wmi, asked: asked).RunAsync(DeleteJob(), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Succeeded");
        wmi.Deleted.Should().ContainSingle().Which.Text("SID").Should().Be(TargetSid);
        wmi.Queries.Should().Contain(q => q.Contains("Win32_UserProfile") && q.Contains("SID = 'S-1-5-21-1-2-3-4001'"));
        asked.Should().ContainSingle().Which.Should().Be(@"\\PC-1\C$\Users\old.user", "the folder is checked over the admin share before anything is deleted");
    }

    [Fact]
    public async Task Profile_delete_refuses_loaded_special_moved_and_own_profiles_without_deleting()
    {
        async Task<(ActionOutcome, FakeWmi)> Run(FakeWmi wmi, JobPayload job) =>
            (await Action(wmi).RunAsync(job, new Sink(), CancellationToken.None), wmi);

        var (loaded, w1) = await Run(ProfileHost(loaded: true), DeleteJob());
        var (special, w2) = await Run(ProfileHost(special: true), DeleteJob());
        var (moved, w3) = await Run(ProfileHost(path: @"C:\Users\someone.else"), DeleteJob());
        var (own, w4) = await Run(ProfileHost(), DeleteJob(initiator: TargetSid));

        loaded.Summary.Should().Contain("signed in");
        special.Summary.Should().Contain("system profile");
        moved.Summary.Should().Contain("Refused");
        own.Summary.Should().Contain("your own profile");
        new[] { loaded, special, moved, own }.Should().OnlyContain(o => o.State == "Failed");
        new[] { w1, w2, w3, w4 }.Should().OnlyContain(w => w.Deleted.Count == 0);
        w4.Queries.Should().BeEmpty("the own-profile check happens before any WMI call");
    }

    [Theory]
    [InlineData(2)]    // interactive
    [InlineData(10)]   // remote interactive (RDP)
    [InlineData(11)]   // cached interactive
    public async Task A_user_in_an_interactive_session_is_refused_even_when_the_profile_is_not_flagged_loaded(int logonType)
    {
        var wmi = ProfileHost(loaded: false);
        SignedIn(wmi, TargetSid, logonType);

        var outcome = await Action(wmi).RunAsync(DeleteJob(), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Failed");
        outcome.Summary.Should().Contain("signed in");
        wmi.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task Other_users_and_service_sessions_do_not_block_the_deletion()
    {
        var wmi = ProfileHost();
        SignedIn(wmi, "S-1-5-21-1-2-3-7777");                      // someone else is signed in
        (await Action(wmi).RunAsync(DeleteJob(), new Sink(), CancellationToken.None)).State.Should().Be("Succeeded");

        var service = ProfileHost();
        SignedIn(service, TargetSid, logonType: 5);                 // a service logon of the same SID is not a person at the keyboard
        (await Action(service).RunAsync(DeleteJob(), new Sink(), CancellationToken.None)).State.Should().Be("Succeeded");
    }

    [Theory]
    [InlineData("Win32_LogonSession")]
    [InlineData("ASSOCIATORS OF")]
    public async Task When_who_is_signed_in_cannot_be_read_nothing_is_deleted(string failing)
    {
        var wmi = ProfileHost();
        SignedIn(wmi, "S-1-5-21-1-2-3-7777");
        wmi.ThrowWhenQueryContains = failing;

        var outcome = await Action(wmi).RunAsync(DeleteJob(), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Failed");
        outcome.Summary.Should().Contain("could not verify who is signed in");
        wmi.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_profile_folder_that_is_a_link_is_refused()
    {
        var wmi = ProfileHost();
        var outcome = await Action(wmi, FileAttributes.Directory | FileAttributes.ReparsePoint).RunAsync(DeleteJob(), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Failed");
        outcome.Summary.Should().Contain("reparse point");
        wmi.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_folder_that_cannot_be_checked_refuses_and_a_missing_folder_does_not()
    {
        var denied = ProfileHost();
        var refused = await new DeleteProfileAction(_ => denied, _ => throw new UnauthorizedAccessException()).RunAsync(DeleteJob(), new Sink(), CancellationToken.None);
        refused.State.Should().Be("Failed");
        refused.Summary.Should().Contain("could not be checked");
        denied.Deleted.Should().BeEmpty();

        var missing = ProfileHost();   // the folder is already gone: only the registration is left to remove
        (await Action(missing, folder: null).RunAsync(DeleteJob(), new Sink(), CancellationToken.None)).State.Should().Be("Succeeded");
        missing.Deleted.Should().ContainSingle();
    }

    [Theory]
    [InlineData(@"C:\Users\Default")]
    [InlineData(@"C:\Users\Public")]
    [InlineData(@"C:\Users\All Users")]
    [InlineData(@"C:\Users\Administrator")]
    [InlineData(@"C:\Users\systemprofile")]
    public async Task Default_public_system_and_Administrator_folders_are_refused_before_any_wmi_call(string path)
    {
        var wmi = ProfileHost(path: path);
        var outcome = await Action(wmi).RunAsync(DeleteJob(path: path), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Failed");
        wmi.Queries.Should().BeEmpty();
        wmi.Deleted.Should().BeEmpty();
    }

    [Theory]
    [InlineData(@"C:\Users\.. ")]
    [InlineData(@"C:\Users\Public ")]
    [InlineData(@"C:\Users\Default.")]
    [InlineData(@"C:\Users\x.")]
    [InlineData(@"C:\Users\ADMINI~1")]
    [InlineData(@"C:\Users\DEFAUL~1")]
    [InlineData(@"C:\Users\%USERPROFILE%")]
    [InlineData("C:\\Users\\\u00dcnal")]
    [InlineData(@"C:\Users\ x")]
    [InlineData("C:\\Users\\x\u0001")]
    [InlineData("C:\\Users\\x\n")]
    public async Task Folder_names_that_windows_normalises_or_with_control_characters_are_refused_before_any_wmi_call(string path)
    {
        var wmi = ProfileHost(path: path);
        var outcome = await Action(wmi).RunAsync(DeleteJob(path: path), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Failed");
        wmi.Queries.Should().BeEmpty();
        wmi.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_profile_whose_folder_is_also_registered_for_another_profile_is_refused()
    {
        var wmi = new FakeWmi();
        wmi.Rows["Win32_UserProfile"] =
        [
            Row("Win32_UserProfile", ("SID", TargetSid), ("LocalPath", @"C:\Users\bob"), ("Loaded", false), ("Special", false)),
            Row("Win32_UserProfile", ("SID", "S-1-5-18"), ("LocalPath", @"c:\users\BOB\"), ("Loaded", false), ("Special", true)),
        ];

        var outcome = await Action(wmi).RunAsync(DeleteJob(path: @"C:\Users\bob"), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Failed");
        outcome.Summary.Should().Contain("same folder");
        wmi.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task The_built_in_administrator_sid_is_refused_whatever_the_folder_is_called()
    {
        var wmi = ProfileHost();
        var outcome = await Action(wmi).RunAsync(DeleteJob(sid: "S-1-5-21-1-2-3-500"), new Sink(), CancellationToken.None);
        outcome.State.Should().Be("Failed");
        wmi.Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task The_summary_reports_the_servers_last_measurement_as_the_size_freed_when_it_is_known()
    {
        var withSize = await Action(ProfileHost()).RunAsync(DeleteJob(size: "3221225472"), new Sink(), CancellationToken.None);
        var without = await Action(ProfileHost()).RunAsync(DeleteJob(), new Sink(), CancellationToken.None);
        var garbage = await Action(ProfileHost()).RunAsync(DeleteJob(size: "-5; drop"), new Sink(), CancellationToken.None);

        withSize.Summary.Should().Contain("3,072 MB").And.Contain("3221225472").And.Contain("last size measurement");
        without.Summary.Should().NotContain("freed");
        garbage.State.Should().Be("Succeeded");
        garbage.Summary.Should().NotContain("freed");
    }

    [Theory]
    [InlineData("S-1-5-18", @"C:\Users\x")]
    [InlineData("S-1-5-21-1-2-3-4001", @"C:\Windows\System32")]
    [InlineData("S-1-5-21-1-2-3-4001", @"C:\Users\..\Windows")]
    [InlineData("S-1-5-21-1-2-3-4001", @"C:\Users\a\b")]
    [InlineData("S-1-5-21-1-2-3-4001'; --", @"C:\Users\x")]
    public async Task Profile_delete_refuses_system_sids_and_non_profile_paths_before_any_wmi_call(string sid, string path)
    {
        var wmi = ProfileHost();
        var outcome = await Action(wmi).RunAsync(DeleteJob(sid, path), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Failed");
        wmi.Queries.Should().BeEmpty();
        wmi.Deleted.Should().BeEmpty();
    }

    // ---------- sessions and input rules

    [Theory]
    [InlineData("session.rdp", "mstsc.exe", "/v:PC-1")]
    [InlineData("session.assist", "msra.exe", "/offerra PC-1")]
    public async Task Sessions_launch_the_right_tool_on_the_operators_desktop(string code, string file, string args)
    {
        var launched = new List<(string, string)>();
        var outcome = await new SessionAction((f, a) => launched.Add((f, a))).RunAsync(Job(code), new Sink(), CancellationToken.None);

        outcome.State.Should().Be("Succeeded");
        launched.Should().Equal((file, args));
    }

    [Theory]
    [InlineData("PC-1; calc")]
    [InlineData("PC 1")]
    [InlineData("-bad")]
    [InlineData("pc\n")]
    public async Task Hosts_with_unsafe_characters_are_refused_by_every_action(string host)
    {
        var launched = new List<(string, string)>();
        var wmi = new FakeWmi();

        (await new SessionAction((f, a) => launched.Add((f, a))).RunAsync(Job("session.rdp", host), new Sink(), CancellationToken.None)).State.Should().Be("Failed");
        (await new ServiceAction(_ => wmi).RunAsync(Job("svc.start", host, p: ("serviceName", "Spooler")), new Sink(), CancellationToken.None)).State.Should().Be("Failed");
        (await Action(wmi).RunAsync(DeleteJob() with { Host = host }, new Sink(), CancellationToken.None)).State.Should().Be("Failed");

        launched.Should().BeEmpty();
        wmi.Queries.Should().BeEmpty();
    }

    [Fact]
    public void The_helper_knows_every_implemented_catalog_action_it_runs_and_nothing_else_from_the_old_path()
    {
        foreach (var code in new[] { "svc.start", "svc.stop", "svc.restart", "proc.kill", "profile.delete", "session.rdp", "session.assist", "profile.sizes" })
            ActionRegistry.IsKnown(code).Should().BeTrue(code);
        foreach (var legacy in new[] { "RestartService", "KillProcess", "DeleteProfile", "RunScript", "RemoteSession" })
            ActionRegistry.IsKnown(legacy).Should().BeFalse(legacy);
    }
}
