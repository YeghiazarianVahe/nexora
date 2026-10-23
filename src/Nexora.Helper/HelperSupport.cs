using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Win32;
using Nexora.Contracts;

namespace Nexora.Helper;

public sealed class ForeverRetryPolicy : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext c) => Backoff.After(c.PreviousRetryCount);
}

/// <summary>Retry delays that grow from 2 s to a minute, so a helper that cannot reach the server neither hammers it nor gives up.</summary>
public static class Backoff
{
    public static TimeSpan After(long previousAttempts) => TimeSpan.FromSeconds(Math.Min(60, 2 * Math.Pow(2, Math.Min(previousAttempts, 5))));

    /// <summary>How long a person without the Operator role waits before the helper asks again. With the all-users startup every user's helper
    /// runs; retrying a refusal every minute for ever would only fill the server log.</summary>
    public static readonly TimeSpan RoleRefused = TimeSpan.FromHours(1);

    /// <summary>The delay after a failed attempt: the usual growing delay, or an hour when the server said this account has no Operator role.</summary>
    public static TimeSpan After(long previousAttempts, ProbeKind? lastFailure) => lastFailure == ProbeKind.RoleTooLow ? RoleRefused : After(previousAttempts);
}

/// <param name="Server">Where Nexora is. One value: --server, else NEXORA_SERVER, else the pilot server.</param>
/// <param name="Command">run (default), install, uninstall or status.</param>
public sealed record HelperOptions(string Server, bool Hidden, string Command = "run")
{
    public static HelperOptions Parse(string[] args, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var server = env("NEXORA_SERVER") is { Length: > 0 } fromEnv ? fromEnv : HelperDefaults.Server;
        for (var i = 0; i < args.Length; i++)
            if (args[i] == "--server" && i + 1 < args.Length) server = args[++i];
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException($"Invalid --server '{server}'");

        var command = args.Contains("--install") ? "install" : args.Contains("--uninstall") ? "uninstall" : args.Contains("--status") ? "status" : "run";
        return new HelperOptions(server.TrimEnd('/'), args.Contains("--hidden"), command);
    }
}

/// <summary>Starts the helper at every sign-in of the current user (HKCU Run key): no administrator rights, nothing machine-wide.</summary>
public static class AutoStart
{
    public const string DefaultKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string CommandFor(string exePath, string server) => $"\"{exePath}\" --server {server} --hidden";

    public static string Install(string exePath, string server, string keyPath = DefaultKey)
    {
        var command = CommandFor(exePath, server);
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        key.SetValue(HelperDefaults.RunValueName, command, RegistryValueKind.String);
        return command;
    }

    public static bool Uninstall(string keyPath = DefaultKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        if (key?.GetValue(HelperDefaults.RunValueName) is null) return false;
        key.DeleteValue(HelperDefaults.RunValueName, throwOnMissingValue: false);
        return true;
    }

    public static string? Current(string keyPath = DefaultKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(HelperDefaults.RunValueName) as string;
    }
}

public sealed record HelperStatus(string State, string Server, string Message, DateTime AtUtc);

/// <summary>
/// What the helper tells its operator when nobody is looking at a console: a small status file and a log, next to the pinned
/// settings in %LOCALAPPDATA%\Nexora. "Nexora.Helper.exe --status" prints the status.
/// </summary>
public sealed class HelperState(string? directory = null)
{
    private readonly string _dir = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nexora");
    private readonly Lock _gate = new();
    public string StatusFile => Path.Combine(_dir, "helper-status.json");
    public string LogFile => Path.Combine(_dir, "helper.log");

    public void Set(string state, string server, string message)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            lock (_gate) File.WriteAllText(StatusFile, JsonSerializer.Serialize(new HelperStatus(state, server, message, DateTime.UtcNow)));
        }
        catch (IOException) { /* a status file is a convenience */ }
        catch (UnauthorizedAccessException) { }
    }

    public HelperStatus? Read()
    {
        try { return File.Exists(StatusFile) ? JsonSerializer.Deserialize<HelperStatus>(File.ReadAllText(StatusFile)) : null; }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    public void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            lock (_gate)
            {
                var info = new FileInfo(LogFile);
                if (info.Exists && info.Length > 512 * 1024) File.Move(LogFile, LogFile + ".old", overwrite: true);
                File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public enum ProbeKind { Reachable, Unreachable, NotNexora, SignInRejected, RoleTooLow }

public sealed record ProbeResult(ProbeKind Kind, string Message)
{
    public bool Ok => Kind == ProbeKind.Reachable;
}

/// <summary>
/// When the helper cannot connect, say why in words an operator can act on: nothing there, something else there (the wrong port),
/// Windows sign-in refused, or an account without the Operator role.
/// </summary>
public static class ServerProbe
{
    public static async Task<ProbeResult> CheckAsync(string server, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        using var http = handler is null ? new HttpClient(new HttpClientHandler { UseDefaultCredentials = true }) : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(8);
        try
        {
            using var health = await http.GetAsync($"{server}/health", ct);
            var body = health.IsSuccessStatusCode ? await health.Content.ReadAsStringAsync(ct) : "";
            if (!health.IsSuccessStatusCode || !body.Contains("\"nexora\"", StringComparison.OrdinalIgnoreCase))
                return new(ProbeKind.NotNexora, $"Something answered at {server} (HTTP {(int)health.StatusCode}), but it is not the Nexora server. Check the address and port; the pilot server is {HelperDefaults.Server}.");

            using var negotiate = await http.PostAsync($"{server}/hubs/helper/negotiate?negotiateVersion=1", null, ct);
            return negotiate.StatusCode switch
            {
                HttpStatusCode.OK => new(ProbeKind.Reachable, $"Nexora at {server} accepts this helper."),
                HttpStatusCode.Unauthorized => new(ProbeKind.SignInRejected, $"Nexora at {server} did not accept your Windows sign-in. Open {server} in your browser once to check that single sign-on works for you."),
                HttpStatusCode.Forbidden => new(ProbeKind.RoleTooLow, $"Nexora at {server} knows you but your account has no Operator role, so it refuses the helper. Ask an administrator for the Operator role."),
                var code => new(ProbeKind.NotNexora, $"{server} answered the helper channel with HTTP {(int)code}; this does not look like the Nexora server."),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            return new(ProbeKind.Unreachable, $"Can't reach the Nexora server at {server} ({(ex.InnerException ?? ex).Message}). Check the address (--server) and that this PC is on the bank network.");
        }
    }
}

/// <summary>Lets a windowless (WinExe) program print to the terminal it was started from, when there is one.</summary>
public static class ConsoleAttach
{
    private const int AttachParentProcess = -1, StdOutput = -11, StdError = -12;

    public static bool TryAttach()
    {
        try
        {
            if (!AttachConsole(AttachParentProcess)) return false;
            var h = CreateFile("CONOUT$", 0x40000000 /* GENERIC_WRITE */, 2 /* FILE_SHARE_WRITE */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
            if (h == new IntPtr(-1)) return false;
            SetStdHandle(StdOutput, h);
            SetStdHandle(StdError, h);
            var writer = new StreamWriter(new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(h, ownsHandle: false), FileAccess.Write), System.Text.Encoding.UTF8) { AutoFlush = true };
            Console.SetOut(writer);
            Console.SetError(writer);
            return true;
        }
        catch (Exception) { return false; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(int processId);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetStdHandle(int stdHandle, IntPtr handle);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}
