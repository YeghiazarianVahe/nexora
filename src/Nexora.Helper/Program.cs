using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.AspNetCore.SignalR.Client;
using Nexora.Contracts;
using Nexora.Helper;

// Nexora.Helper: runs in the operator's own Windows session, connects to Nexora with the operator's
// Windows identity, and runs the jobs the server sends for that operator with the operator's own token.
//
//   Nexora.Helper.exe [--server http://wks-111:5090] [--hidden]     run (the server defaults to the pilot)
//   Nexora.Helper.exe --install [--server URL] [--no-run]            start at every sign-in of THIS user (no admin rights), and start now
//   Nexora.Helper.exe --uninstall                                    remove that autostart
//   Nexora.Helper.exe --status                                       what the helper last told its operator

var options = HelperOptions.Parse(args);
// The helper has no console of its own (it must never flash a window at sign-in). Run from a terminal, it borrows that terminal for --status,
// --install and --uninstall so they can still print.
if (options.Command != "run" || args.Contains("--help")) ConsoleAttach.TryAttach();
var state = new HelperState();
var me = WindowsIdentity.GetCurrent();
var mySid = me.User!.Value;
var myName = me.Name;
var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Nexora.Helper.exe");

switch (options.Command)
{
    case "status":
        var last = state.Read();
        Console.WriteLine(last is null
            ? "The helper has not run for this user yet."
            : $"{last.State}: {last.Message}\n(server {last.Server}, {last.AtUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss})");
        Console.WriteLine($"Autostart: {AutoStart.Current() ?? "not installed"}");
        Console.WriteLine($"Log: {state.LogFile}");
        return;
    case "uninstall":
        Console.WriteLine(AutoStart.Uninstall() ? "Autostart removed." : "There was no autostart entry.");
        return;
    case "install":
        Console.WriteLine($"Autostart installed for {myName}: {AutoStart.Install(exe, options.Server)}");
        if (args.Contains("--no-run")) return;
        Console.WriteLine("It starts at your next sign-in; starting it now...");
        break;
}

if (options.Hidden) HideConsole();

// One helper per signed-in user: an autostart entry and a manual start must not fight over the connection.
using var single = new Mutex(initiallyOwned: true, $@"Local\Nexora.Helper.{mySid}", out var firstInstance);
if (!firstInstance)
{
    Log("A Nexora helper is already running in this session.");
    return;
}

Log($"Nexora.Helper for {myName} ({mySid}) -> {options.Server}");
state.Set("connecting", options.Server, "Starting.");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var connection = new HubConnectionBuilder()
    .WithUrl(new Uri(new Uri(options.Server), "/hubs/helper?machine=" + Uri.EscapeDataString(Environment.MachineName)), o => o.UseDefaultCredentials = true)
    .WithAutomaticReconnect(new ForeverRetryPolicy())
    .Build();

// Jobs addressed to this operator, accepted only when signed by the server key pinned in this executable.
var pinned = TrustStamp.ReadOwn();
Log(pinned is null
    ? "WARNING: no server key is pinned in this helper, so every job will be refused. Download the helper again from Nexora."
    : $"Pinned server key {JobTrust.Fingerprint(pinned)}.");
var jobs = new HelperJobs(connection, mySid, pinned, Log);
connection.On<JobEnvelope>("ExecuteJobV2", envelope => _ = jobs.HandleAsync(envelope, cts.Token));
connection.On<Guid>("CancelJobV2", targetId => jobs.Cancel(targetId));
connection.Reconnecting += ex => { Log($"Connection lost: {ex?.Message}. Reconnecting..."); state.Set("connecting", options.Server, $"Connection lost ({ex?.Message}); reconnecting."); return Task.CompletedTask; };
connection.Reconnected += _ => { Log("Reconnected."); state.Set("connected", options.Server, "Connected."); return Task.CompletedTask; };
var flaps = 0;
var connectedAt = DateTime.MinValue;
connection.Closed += async ex =>
{
    if (cts.IsCancellationRequested) return;
    // A connection that dies within seconds is a flap, not a success: back off instead of reconnecting in a tight loop.
    flaps = DateTime.UtcNow - connectedAt < TimeSpan.FromSeconds(30) ? Math.Min(flaps + 1, 6) : 0;
    Log($"Connection closed{(string.IsNullOrEmpty(ex?.Message) ? "" : ": " + ex.Message)}. Connecting again...");
    await ConnectAsync(flaps);
};

await ConnectAsync();
if (cts.IsCancellationRequested) return;

try { await Task.Delay(Timeout.Infinite, cts.Token); }
catch (OperationCanceledException) { }
await connection.StopAsync();
state.Set("stopped", options.Server, "The helper was stopped.");
Log("Stopped.");

/// <summary>
/// Checks that the address really is Nexora (and that Nexora accepts this user), then connects; with growing delays (2 s up to a minute) when it cannot,
/// saying why: nothing there, another application there (the wrong port), a refused Windows sign-in, an account without the Operator role.
/// </summary>
async Task ConnectAsync(long startAttempt = 0)
{
    ProbeKind? lastFailure = null;
    for (var attempt = startAttempt; !cts.IsCancellationRequested; attempt++)
    {
        try
        {
            var probe = await ServerProbe.CheckAsync(options.Server, ct: cts.Token);
            lastFailure = probe.Ok ? null : probe.Kind;
            if (!probe.Ok)
            {
                // Explain the first failure and then every few attempts; in between, one short line.
                if (attempt == startAttempt || attempt % 5 == 0)
                {
                    Log(probe.Message);
                    state.Set(probe.Kind == ProbeKind.Unreachable ? "unreachable" : "rejected", options.Server, probe.Message);
                }
                else Log("Still cannot connect.");
            }
            else
            {
                await connection.StartAsync(cts.Token);
                connectedAt = DateTime.UtcNow;
                Log("Connected. Waiting for jobs.");
                state.Set("connected", options.Server, "Connected.");
                return;
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Log($"Cannot connect yet ({ex.Message}).");
            state.Set("connecting", options.Server, $"Cannot connect yet: {ex.Message}");
        }
        try { await Task.Delay(Backoff.After(attempt, lastFailure), cts.Token); } catch (OperationCanceledException) { return; }
    }
}

void Log(string message)
{
    Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");
    state.Log(message);
}

static void HideConsole()
{
    var handle = GetConsoleWindow();
    if (handle != IntPtr.Zero) ShowWindow(handle, 0);
}

[DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
[DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
