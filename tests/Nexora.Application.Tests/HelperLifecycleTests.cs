using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Win32;
using Nexora.Contracts;
using Nexora.Helper;

namespace Nexora.Application.Tests;

public sealed class HelperLifecycleTests
{
    // ---------- one address

    [Fact]
    public void Without_any_setting_the_helper_goes_to_the_pilot_on_5090_never_5080()
    {
        var o = HelperOptions.Parse([], env: _ => null);

        o.Server.Should().Be("http://wks-111:5090");
        HelperDefaults.Server.Should().NotContain("5080");
        HelperDefaults.Port.Should().Be(5090);
        o.Command.Should().Be("run");
    }

    [Fact]
    public void The_server_comes_from_the_flag_then_the_environment_then_the_default()
    {
        HelperOptions.Parse(["--server", "http://other:7000/"], env: _ => "http://fromenv:1").Server.Should().Be("http://other:7000");
        HelperOptions.Parse([], env: n => n == "NEXORA_SERVER" ? "http://fromenv:1" : null).Server.Should().Be("http://fromenv:1");
        HelperOptions.Parse(["--hidden"], env: _ => "").Should().Be(new HelperOptions("http://wks-111:5090", true));
    }

    [Theory]
    [InlineData("wks-111:5090")]
    [InlineData("ftp://x")]
    [InlineData("")]
    public void A_server_that_is_not_an_http_address_is_refused(string bad) =>
        FluentActions.Invoking(() => HelperOptions.Parse(["--server", bad], env: _ => null)).Should().Throw<ArgumentException>();

    [Theory]
    [InlineData("--install", "install")]
    [InlineData("--uninstall", "uninstall")]
    [InlineData("--status", "status")]
    public void Commands_are_recognised(string flag, string command) =>
        HelperOptions.Parse([flag], env: _ => null).Command.Should().Be(command);

    // ---------- reconnect timing

    [Fact]
    public void Retries_back_off_from_two_seconds_to_a_minute_and_never_stop()
    {
        Enumerable.Range(0, 8).Select(i => Backoff.After(i).TotalSeconds).Should().Equal(2, 4, 8, 16, 32, 60, 60, 60);
        new ForeverRetryPolicy().NextRetryDelay(new Microsoft.AspNetCore.SignalR.Client.RetryContext { PreviousRetryCount = 500 }).Should().Be(TimeSpan.FromMinutes(1));
        Backoff.After(3, ProbeKind.RoleTooLow).Should().Be(TimeSpan.FromHours(1), "an account without the Operator role is not asked again every minute");
        Backoff.After(9, ProbeKind.RoleTooLow).Should().Be(TimeSpan.FromHours(1));
        Backoff.After(9, ProbeKind.Unreachable).Should().Be(TimeSpan.FromSeconds(60), "other failures keep the normal capped delay");
        Backoff.After(2, null).Should().Be(Backoff.After(2));
    }

    // ---------- autostart for the current user only

    [Fact]
    public void Install_registers_a_per_user_Run_value_and_uninstall_removes_it()
    {
        var key = $@"Software\NexoraTests\{Guid.NewGuid():N}\Run";
        try
        {
            var command = AutoStart.Install(@"C:\Users\vazgen\AppData\Local\Nexora\Nexora.Helper.exe", "http://wks-111:5090", key);

            command.Should().Be("\"C:\\Users\\vazgen\\AppData\\Local\\Nexora\\Nexora.Helper.exe\" --server http://wks-111:5090 --hidden");
            AutoStart.Current(key).Should().Be(command);
            using (var hkcu = Registry.CurrentUser.OpenSubKey(key)) hkcu!.GetValue(HelperDefaults.RunValueName).Should().Be(command, "it lives under HKEY_CURRENT_USER only");

            AutoStart.Install(@"D:\other\Nexora.Helper.exe", "http://wks-111:5090", key);
            AutoStart.Current(key).Should().Contain(@"D:\other\", "installing again replaces the entry instead of adding a second");

            AutoStart.Uninstall(key).Should().BeTrue();
            AutoStart.Current(key).Should().BeNull();
            AutoStart.Uninstall(key).Should().BeFalse("nothing left to remove");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree($@"Software\NexoraTests", throwOnMissingSubKey: false); }
    }

    [Fact]
    public void The_real_autostart_key_is_the_per_user_Run_key()
    {
        AutoStart.DefaultKey.Should().Be(@"Software\Microsoft\Windows\CurrentVersion\Run");
        AutoStart.CommandFor(@"C:\x\Nexora.Helper.exe", "http://wks-111:5090").Should().EndWith("--hidden");
    }

    // ---------- say why it cannot connect

    /// <summary>A tiny HTTP server on a loopback port that answers by path, so the probe can be tested without a real Nexora.</summary>
    private sealed class FakeServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string Url => $"http://127.0.0.1:{Port}";

        public FakeServer(Func<string, string, (int Status, string Body)> respond)
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_stop.Token); } catch { return; }
                    _ = Task.Run(async () =>
                    {
                        using var c = client;
                        var stream = c.GetStream();
                        var buffer = new byte[4096];
                        var n = await stream.ReadAsync(buffer);
                        var head = Encoding.ASCII.GetString(buffer, 0, n).Split("\r\n")[0].Split(' ');
                        var (status, body) = respond(head[0], head[1]);
                        var bytes = Encoding.UTF8.GetBytes(body);
                        var text = $"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(text));
                        await stream.WriteAsync(bytes);
                    });
                }
            });
        }

        public void Dispose() { _stop.Cancel(); _listener.Stop(); }
    }

    private static int FreeClosedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task A_helper_pointed_at_nothing_says_it_cannot_reach_the_server_at_that_address()
    {
        var url = $"http://127.0.0.1:{FreeClosedPort()}";

        var result = await ServerProbe.CheckAsync(url);

        result.Kind.Should().Be(ProbeKind.Unreachable);
        result.Message.Should().StartWith($"Can't reach the Nexora server at {url}").And.Contain("--server");
    }

    [Fact]
    public async Task A_helper_pointed_at_another_application_says_the_port_is_wrong()
    {
        using var other = new FakeServer((_, _) => (404, "Not Found"));   // e.g. the Service Desk Console on 5080

        var result = await ServerProbe.CheckAsync(other.Url);

        result.Kind.Should().Be(ProbeKind.NotNexora);
        result.Message.Should().Contain("not the Nexora server").And.Contain(HelperDefaults.Server);
    }

    [Fact]
    public async Task Another_application_that_also_answers_health_ok_is_still_not_Nexora()
    {
        using var other = new FakeServer((_, path) => path == "/health" ? (200, "{\"status\":\"ok\"}") : (200, "{}"));   // an app that happens to look healthy and even accept a hub

        var result = await ServerProbe.CheckAsync(other.Url);

        result.Kind.Should().Be(ProbeKind.NotNexora);
    }

    [Theory]
    [InlineData(200, ProbeKind.Reachable, "accepts this helper")]
    [InlineData(401, ProbeKind.SignInRejected, "Windows sign-in")]
    [InlineData(403, ProbeKind.RoleTooLow, "Operator role")]
    [InlineData(500, ProbeKind.NotNexora, "HTTP 500")]
    public async Task The_probe_explains_what_Nexora_said_to_the_helper_channel(int negotiateStatus, ProbeKind kind, string words)
    {
        using var nexora = new FakeServer((method, path) => path == "/health" ? (200, "{\"status\":\"ok\",\"app\":\"nexora\"}") : method == "POST" && path.StartsWith("/hubs/helper/negotiate") ? (negotiateStatus, "{}") : (404, ""));

        var result = await ServerProbe.CheckAsync(nexora.Url);

        result.Kind.Should().Be(kind);
        result.Message.Should().Contain(words);
    }

    // ---------- what the helper leaves for its operator

    [Fact]
    public void The_helper_writes_a_status_file_and_a_log_that_status_can_read()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexora-helper-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var state = new HelperState(dir);
            state.Read().Should().BeNull();

            state.Set("unreachable", "http://x:1", "Can't reach the Nexora server at http://x:1 (refused).");
            state.Log("first line");

            var read = state.Read()!;
            (read.State, read.Server).Should().Be(("unreachable", "http://x:1"));
            read.Message.Should().StartWith("Can't reach");
            File.ReadAllText(state.LogFile).Should().Contain("first line");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}

/// <summary>The helper must never open a console window, however it is started.</summary>
public sealed class WindowlessHelperTests
{
    /// <summary>The "Subsystem" field of the PE header: 2 is a Windows GUI program (no console), 3 is a console program.</summary>
    private static int Subsystem(string exe)
    {
        using var fs = File.OpenRead(exe);
        using var r = new BinaryReader(fs);
        fs.Seek(0x3C, SeekOrigin.Begin);
        var pe = r.ReadInt32();
        fs.Seek(pe + 4 + 20 + 68, SeekOrigin.Begin); // signature + COFF header + offset of Subsystem in the optional header
        return r.ReadUInt16();
    }

    [Fact]
    public void The_helper_executable_is_a_windows_program_without_a_console()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "Nexora.Helper.exe");
        File.Exists(exe).Should().BeTrue();
        Subsystem(exe).Should().Be(2, "a console program would flash a window at every sign-in");
    }

    [Fact]
    public void The_project_says_so_too()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Nexora.sln"))) dir = dir.Parent;
        File.ReadAllText(Path.Combine(dir!.FullName, "src", "Nexora.Helper", "Nexora.Helper.csproj")).Should().Contain("<OutputType>WinExe</OutputType>");
    }
}
