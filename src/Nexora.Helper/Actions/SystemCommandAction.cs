using System.Text.RegularExpressions;
using Nexora.Contracts;

namespace Nexora.Helper.Actions;

/// <summary>
/// The built-in maintenance actions that run ON the target as SYSTEM (sfc, DISM, chkdsk, clock, policy, Defender, ...). Each one is a fixed,
/// reviewed PowerShell body in this file: nothing the server or the operator types is ever executed as code. The few parameters (a UNC source, a
/// restart flag, a message) are validated again here and then only ever placed inside quotes. They run through the same one-off SYSTEM task and
/// UTF-8 output file as <see cref="ScriptRunAction"/>, created over RPC with the operator's own rights (no WinRM).
/// </summary>
public sealed partial class SystemCommandAction(ScriptRunAction? runner = null) : IJobAction
{
    private readonly ScriptRunAction _runner = runner ?? new ScriptRunAction();

    public IReadOnlyList<string> Codes => Commands.Keys.ToList();

    [GeneratedRegex(@"^\\\\[A-Za-z0-9][A-Za-z0-9._\-]{0,62}(\\[^""<>|?*\x00-\x1F\\]{1,128}){1,8}\.(wim|esd)\z", RegexOptions.IgnoreCase)]
    private static partial Regex SourcePattern();

    /// <summary>What a command is: the PowerShell it runs and how its exit code reads.</summary>
    internal sealed record Command(Func<IReadOnlyDictionary<string, string>, (string? Body, string? Error)> Build, Func<int, ActionOutcome>? Exit = null);

    private static (string?, string?) Fixed(string body) => (body, null);

    // sfc writes UTF-16 even to a pipe; the NULs it leaves behind are removed so the text reads normally.
    private const string StripNul = "ForEach-Object { $_ -replace \"`0\", '' }";

    private static ActionOutcome Dism(int code) => code switch
    {
        0 => Outcomes.Ok("Completed successfully."),
        3010 => new ActionOutcome("Warning", 3010, "Completed; a restart is required to finish."),
        _ => new ActionOutcome("Failed", code, $"DISM reported error {code} (0x{code:X8})."),
    };

    // shutdown.exe 1190: a shutdown is already scheduled.
    private static Func<int, ActionOutcome> PowerExit(string verb) => c => c switch
    {
        0 => Outcomes.Ok($"The PC will {verb} as scheduled. Run \"Cancel pending restart\" to stop it."),
        1190 => new ActionOutcome("Warning", c, "A restart or shutdown is already scheduled on this PC."),
        _ => new ActionOutcome("Failed", c, $"The {verb} could not be scheduled (exit {c})."),
    };

    private static (string?, string?) Power(IReadOnlyDictionary<string, string> p, string flag, string verb)
    {
        var minutes = 0;
        if (p.GetValueOrDefault("delayMinutes") is { Length: > 0 } d && (!int.TryParse(d, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out minutes) || minutes > 60))
            return (null, "The delay must be a whole number of minutes from 0 to 60.");
        var message = p.GetValueOrDefault("message") ?? "";
        if (message.Length > 255 || message.Any(char.IsControl)) return (null, "The message must be at most 255 characters without control characters.");
        var text = message.Length > 0 ? message : $"Nexora: this PC will {verb} on request of the IT Service Desk.";
        var seconds = Math.Max(10, minutes * 60);
        return Fixed($"& \"$env:SystemRoot\\System32\\shutdown.exe\" {flag} /t {seconds} /c '{text.Replace("'", "''")}'\nexit $LASTEXITCODE");
    }

    internal static readonly IReadOnlyDictionary<string, Command> Commands = new Dictionary<string, Command>(StringComparer.Ordinal)
    {
        ["repair.sfc"] = new(_ => Fixed("& \"$env:SystemRoot\\System32\\sfc.exe\" /scannow 2>&1 | " + StripNul + "\nexit $LASTEXITCODE"),
            c => c == 0 ? Outcomes.Ok("sfc finished. Read the summary above.") : new ActionOutcome("Failed", c, $"sfc could not complete (exit {c}).")),
        ["repair.dism.check"] = new(_ => Fixed("& \"$env:SystemRoot\\System32\\Dism.exe\" /Online /Cleanup-Image /CheckHealth\nexit $LASTEXITCODE"), Dism),
        ["repair.dism.scan"] = new(_ => Fixed("& \"$env:SystemRoot\\System32\\Dism.exe\" /Online /Cleanup-Image /ScanHealth\nexit $LASTEXITCODE"), Dism),
        ["repair.dism.cleanup"] = new(_ => Fixed("& \"$env:SystemRoot\\System32\\Dism.exe\" /Online /Cleanup-Image /StartComponentCleanup\nexit $LASTEXITCODE"), Dism),
        ["repair.dism.restore"] = new(p =>
        {
            var args = "/Online /Cleanup-Image /RestoreHealth";
            if (p.GetValueOrDefault("source") is { Length: > 0 } src)
            {
                if (!SourcePattern().IsMatch(src)) return (null, "The repair source must be a UNC path to an install.wim or install.esd file.");
                var kind = src.EndsWith(".esd", StringComparison.OrdinalIgnoreCase) ? "ESD" : "WIM";
                args += $" '/Source:{kind}:{src.Replace("'", "''")}:1'";
            }
            if (p.GetValueOrDefault("limitAccess") == "true") args += " /LimitAccess";
            return Fixed($"& \"$env:SystemRoot\\System32\\Dism.exe\" {args}\nexit $LASTEXITCODE");
        }, Dism),

        ["disk.chkdsk.scan"] = new(_ => Fixed("& \"$env:SystemRoot\\System32\\chkdsk.exe\" $env:SystemDrive /scan\nexit $LASTEXITCODE"),
            c => c switch
            {
                0 => Outcomes.Ok("No problems found."),
                1 => new ActionOutcome("Warning", 1, "Errors were found and fixed."),
                2 => new ActionOutcome("Warning", 2, "Cleanup was performed."),
                _ => new ActionOutcome("Failed", c, "The disk could not be checked."),
            }),
        // chkdsk /f /r on the system drive cannot run online: it asks to schedule the check for the next boot and its exit code is not a
        // reliable "scheduled" signal (and the prompt is localized). So success is decided by the one locale-independent fact that
        // matters: the boot-time autocheck entry for the system drive is present in BootExecute afterwards.
        // Exit codes: 0 scheduled; 3010 scheduled and a restart is in progress; 1 NOT scheduled (no restart is ever attempted);
        // 2 scheduled but the restart command failed.
        ["disk.chkdsk.fix"] = new(p =>
        {
            var restart = p.GetValueOrDefault("restartNow") == "true"
                ? """
                  shutdown.exe /r /t 60 /c "Nexora: restarting to repair the disk"
                  if ($LASTEXITCODE -ne 0) { Write-Output ('The restart could not be started (exit ' + $LASTEXITCODE + '). The repair is still scheduled for the next restart.'); exit 2 }
                  Write-Output 'The PC restarts in 60 seconds. Run "Cancel pending restart" to stop it.'
                  exit 3010
                  """
                : "Write-Output 'The repair runs at the next restart.'\nexit 0";
            return Fixed("""
                cmd.exe /c "echo Y| chkdsk.exe %SystemDrive% /f /r"
                $chkdskExit = $LASTEXITCODE
                $boot = @((Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name BootExecute -ErrorAction SilentlyContinue).BootExecute)
                $scheduled = @($boot | Where-Object { $_ -match ('autochk.*[\\]\?\?[\\]' +[regex]::Escape($env:SystemDrive)) }).Count -gt 0
                if (-not $scheduled) { Write-Output ('The disk check could not be scheduled (chkdsk exit ' + $chkdskExit + '). The PC was not restarted.'); exit 1 }

                """ + restart);
        },
            c => c switch
            {
                0 => Outcomes.Ok("The disk check is scheduled for the next restart."),
                3010 => new ActionOutcome("Warning", 3010, "The disk check is scheduled and the PC is restarting in 60 seconds."),
                2 => new ActionOutcome("Failed", 2, "The disk check is scheduled, but the restart could not be started."),
                _ => new ActionOutcome("Failed", c, "The disk check could not be scheduled. The PC was not restarted."),
            }),

        // power.restart / power.shutdown: scheduled with shutdown.exe (default 10 s minimum so the job can report back; at most 60 min). The
        // message is validated like session.message and only ever placed in single quotes. Protected workstations, the reason and the typed
        // confirmation are enforced by the catalog (power: true) before a job exists.
        ["power.restart"] = new(p => Power(p, "/r", "restart"), PowerExit("restart")),
        ["power.shutdown"] = new(p => Power(p, "/s", "shut down"), PowerExit("shut down")),

        // Read-only: nothing is changed on the target.
        ["net.diag"] = new(_ => Fixed("""
            $ErrorActionPreference = 'Continue'
            $fail = 0
            Write-Output ('Computer: ' + $env:COMPUTERNAME + '  Domain: ' + $env:USERDNSDOMAIN)
            $cfg = Get-NetIPConfiguration -ErrorAction SilentlyContinue | Where-Object { $_.NetAdapter.Status -eq 'Up' -and $_.IPv4DefaultGateway }
            if (-not $cfg) { Write-Output 'FAIL  No connected adapter with a default gateway.'; $fail++ }
            foreach ($c in $cfg) {
                Write-Output ('Adapter: ' + $c.InterfaceAlias + '  IPv4: ' + (($c.IPv4Address | ForEach-Object { $_.IPAddress }) -join ',') + '  Gateway: ' + $c.IPv4DefaultGateway.NextHop + '  DNS: ' + ($c.DNSServer.ServerAddresses -join ','))
                if (Test-Connection -ComputerName $c.IPv4DefaultGateway.NextHop -Count 2 -Quiet -ErrorAction SilentlyContinue) { Write-Output 'OK    Default gateway answers.' } else { Write-Output 'FAIL  Default gateway does not answer.'; $fail++ }
            }
            $domain = $env:USERDNSDOMAIN
            if ($domain) {
                try { $r = Resolve-DnsName -Name $domain -Type A -ErrorAction Stop; Write-Output ('OK    DNS resolves ' + $domain + ' to ' + (($r | Where-Object IPAddress | ForEach-Object IPAddress) -join ',')) }
                catch { Write-Output ('FAIL  DNS cannot resolve ' + $domain); $fail++ }
                & "$env:SystemRoot\System32\nltest.exe" /dsgetdc:$domain 2>&1 | Select-Object -First 6
                if ($LASTEXITCODE -eq 0) { Write-Output 'OK    A domain controller was located.' } else { Write-Output 'FAIL  No domain controller could be located.'; $fail++ }
            } else { Write-Output 'Not domain joined (no DNS domain); domain checks skipped.' }
            if ($fail -gt 0) { Write-Output ("$fail check(s) failed."); exit 1 }
            Write-Output 'All checks passed.'
            exit 0
            """), c => c == 0 ? Outcomes.Ok("All network checks passed.") : new ActionOutcome("Warning", c, "One or more network checks failed. Read the lines marked FAIL.")),

        // Only files older than 7 days in the Windows temp folder; never user profiles, never the update cache (that needs the update service stopped).
        ["disk.cleanup"] = new(_ => Fixed("""
            $dir = Join-Path $env:SystemRoot 'Temp'
            $limit = (Get-Date).AddDays(-7)
            $before = (Get-PSDrive -Name ($env:SystemDrive.TrimEnd(':'))).Free
            Get-ChildItem -LiteralPath $dir -Recurse -Force -File -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -lt $limit } | Remove-Item -Force -ErrorAction SilentlyContinue
            $freed = (Get-PSDrive -Name ($env:SystemDrive.TrimEnd(':'))).Free - $before
            Write-Output ('Windows temp files older than 7 days were removed. Freed about ' + [Math]::Max(0, [Math]::Round($freed / 1MB)) + ' MB.')
            exit 0
            """)),

        ["system.timesync"] = new(_ => Fixed("& \"$env:SystemRoot\\System32\\w32tm.exe\" /resync /force\nexit $LASTEXITCODE")),
        ["net.gpupdate"] = new(_ => Fixed("& \"$env:SystemRoot\\System32\\gpupdate.exe\" /force /target:computer\nexit $LASTEXITCODE")),
        ["net.gpresult"] = new(_ => Fixed("& \"$env:SystemRoot\\System32\\gpresult.exe\" /r /scope computer\nexit $LASTEXITCODE")),
        ["net.flushdns"] = new(_ => Fixed("& \"$env:SystemRoot\\System32\\ipconfig.exe\" /flushdns\nexit $LASTEXITCODE")),
        ["net.klist"] = new(_ => Fixed("& \"$env:SystemRoot\\System32\\klist.exe\" -li 0x3e7 purge\nexit $LASTEXITCODE")),
        ["power.cancel"] = new(_ => Fixed("& \"$env:SystemRoot\\System32\\shutdown.exe\" /a\nexit $LASTEXITCODE"),
            c => c == 0 ? Outcomes.Ok("The pending restart or shutdown was cancelled.") : new ActionOutcome("Warning", c, "There was no restart or shutdown to cancel.")),

        ["system.pendingreboot"] = new(_ => Fixed("""
            $pending = (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') -or
                       (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired') -or
                       ($null -ne (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name PendingFileRenameOperations -ErrorAction SilentlyContinue))
            Write-Output ('RebootRequired: ' + $pending)
            exit 0
            """)),

        ["defender.signature"] = new(_ => Fixed("Update-MpSignature -ErrorAction Stop\nWrite-Output 'Antivirus definitions updated.'\nexit 0")),
        ["defender.quickscan"] = new(_ => Fixed("Start-MpScan -ScanType QuickScan -ErrorAction Stop\nWrite-Output 'Quick scan finished.'\nexit 0")),

        ["svc.spooler.reset"] = new(_ => Fixed("""
            Stop-Service -Name Spooler -Force -ErrorAction Stop
            Remove-Item -Path "$env:SystemRoot\System32\spool\PRINTERS\*" -Force -ErrorAction SilentlyContinue
            Start-Service -Name Spooler -ErrorAction Stop
            Write-Output 'The print queue was cleared and the spooler restarted.'
            exit 0
            """)),

        ["update.scan"] = new(_ => Fixed("""
            $searcher = (New-Object -ComObject Microsoft.Update.Session).CreateUpdateSearcher()
            $result = $searcher.Search('IsInstalled=0 and IsHidden=0')
            if ($result.Updates.Count -eq 0) { Write-Output 'No updates found'; exit 0 }
            foreach ($u in $result.Updates) { $kb = ($u.KBArticleIDs | ForEach-Object { 'KB' + $_ }) -join ','; Write-Output ("{0}  {1}" -f $kb, $u.Title) }
            exit 0
            """)),
        ["update.history"] = new(_ => Fixed("""
            $searcher = (New-Object -ComObject Microsoft.Update.Session).CreateUpdateSearcher()
            $count = $searcher.GetTotalHistoryCount()
            foreach ($h in $searcher.QueryHistory(0, [Math]::Min($count, 30))) { Write-Output ("{0:yyyy-MM-dd HH:mm}  {1}" -f $h.Date, $h.Title) }
            exit 0
            """)),
    };

    public async Task<ActionOutcome> RunAsync(JobPayload job, IJobSink sink, CancellationToken ct)
    {
        if (!HelperInput.Host(job.Host)) return Outcomes.Fail("Invalid target host.", 2);
        if (!Commands.TryGetValue(job.ActionCode, out var command)) return Outcomes.Fail($"No built-in command for {job.ActionCode}.", 2);
        var (body, error) = command.Build(job.Parameters);
        if (body is null) return Outcomes.Fail(error ?? "Invalid parameters.", 2);
        await sink.Log("system", $"[helper] {job.ActionCode} on {job.Host}, as SYSTEM through a one-off task");
        return await _runner.RunBodyAsync(job, body, sink, ct, command.Exit ?? (c => c == 0 ? Outcomes.Ok("Completed.") : new ActionOutcome("Failed", c, $"The command ended with exit code {c}.")));
    }
}
