<#
.SYNOPSIS
  Checks the Nexora helper on the operator PCs: online, autostart present, helper running, connected to the server, and as whom. No WinRM.

.DESCRIPTION
  Per PC: ping, autostart (the "Nexora Helper" logon task or the all-users Startup shortcut), the helper process and the account that owns it
  (WMI over DCOM), who is signed in (quser), and what the Nexora server says (/api/helper-coverage: connected, operator account, last seen).
  A helper running as anyone other than a person signed in on that PC, or connected to the server as someone else, is flagged WRONG IDENTITY.
  Needs an Admin role in Nexora (for the coverage list) and administrator rights on the PCs.

.EXAMPLE
  .\scripts\verify-helpers.ps1
.EXAMPLE
  .\scripts\verify-helpers.ps1 -Computer wks-44
#>
[CmdletBinding()]
param(
    [string[]]$Computer = @('wks-50', 'wks-007', 'wks-36', 'wks-111', 'wks-44', 'wks-201', 'wks-122', 'wks-75', 'wks-59', 'wks-51', 'wks-25', 'wks-13'),
    [string]$Server = 'http://wks-111:5090',
    [switch]$PassThru
)
$ErrorActionPreference = 'Stop'
$Computer = @($Computer | ForEach-Object { $_ -split '[,;\s]+' } | Where-Object { $_ })

# what the server says
$coverage = @{}
$serverNote = ''
try {
    $c = Invoke-RestMethod -Uri "$Server/api/helper-coverage" -UseDefaultCredentials -UseBasicParsing
    foreach ($p in $c.pcs) { $coverage[$p.pc.ToLowerInvariant()] = $p }
}
catch { $serverNote = "Could not read $Server/api/helper-coverage ($($_.Exception.Message.Trim())); 'connected' is not checked." }

$check = {
    param($pc, $coverage)
    $ErrorActionPreference = 'Stop'
    $r = [ordered]@{ PC = $pc; Online = $false; Autostart = ''; Running = ''; Connected = ''; LastSeen = ''; RunsAs = ''; SignedIn = ''; Problem = '' }
    try {
        $r.Online = [bool](Test-Connection -ComputerName $pc -Count 1 -Quiet)
        if (-not $r.Online) { $r.Problem = 'offline'; return [pscustomobject]$r }

        # autostart
        $auto = @()
        try {
            $svc = New-Object -ComObject 'Schedule.Service'; $svc.Connect($pc)
            [void]$svc.GetFolder('\').GetTask('Nexora Helper'); $auto += 'task'
        }
        catch { }
        if (Test-Path -LiteralPath "\\$pc\C`$\ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp\Nexora Helper.lnk") { $auto += 'startup shortcut' }
        # start.ps1 -InstallHelper uses this user's Run key rather than a machine-wide task.
        if (($pc -split '\.')[0] -ieq $env:COMPUTERNAME) {
            $run = Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name NexoraHelper -ErrorAction SilentlyContinue
            if ($run -and $run.NexoraHelper) { $auto += 'current-user Run key' }
        }
        $r.Autostart = if ($auto) { $auto -join ' + ' } else { 'NONE' }

        # signed in
        $users = @()
        try { $q = & quser.exe /server:$pc 2>$null; if ($LASTEXITCODE -eq 0 -and $q) { $users = @($q | Select-Object -Skip 1 | ForEach-Object { ($_ -replace '^[>\s]+', '') -split '\s{2,}' | Select-Object -First 1 }) } } catch { }
        $r.SignedIn = ($users | Select-Object -Unique) -join ', '

        # process and its owner (DCOM)
        $cim = New-CimSession -ComputerName $pc -SessionOption (New-CimSessionOption -Protocol Dcom) -OperationTimeoutSec 20 -ErrorAction Stop
        $procs = @(Get-CimInstance -CimSession $cim -ClassName Win32_Process -Filter "Name='Nexora.Helper.exe'")
        $owners = @($procs | ForEach-Object { $o = Invoke-CimMethod -CimSession $cim -InputObject $_ -MethodName GetOwner; "$($o.Domain)\$($o.User)" } | Select-Object -Unique)
        Remove-CimSession $cim
        $r.Running = if ($procs.Count -gt 0) { "yes ($($procs.Count))" } elseif ($users) { 'no' } else { 'no (nobody signed in)' }
        $r.RunsAs = $owners -join ', '

        # server's view
        $srv = $coverage[$pc.ToLowerInvariant()]
        if ($coverage.Count -gt 0) {
            $r.Connected = if ($srv) { $srv.state } else { 'not tracked' }
            $r.LastSeen = if ($srv -and $srv.lastSeenUtc) { ([datetime]$srv.lastSeenUtc).ToLocalTime().ToString('dd.MM.yyyy HH:mm') } else { '' }
        }
        else { $r.Connected = 'unchecked' }

        $problems = @()
        # identity: every helper owner must be someone who is signed in there (a stale owner or a different account is wrong)
        foreach ($o in $owners) {
            $short = ($o -split '\\')[-1]
            if ($users.Count -gt 0 -and ($users | ForEach-Object { $_.ToLowerInvariant() }) -notcontains $short.ToLowerInvariant() -and
                -not ($users | Where-Object { $short.ToLowerInvariant().Replace(' ', '').StartsWith($_.ToLowerInvariant()) -or $_.ToLowerInvariant().StartsWith($short.ToLowerInvariant().Replace(' ', '')) })) {
                $problems += "WRONG IDENTITY: helper runs as $o but signed in is $($users -join ', ')"
            }
            if ($o -match 'SYSTEM$|NT AUTHORITY') { $problems += "WRONG IDENTITY: helper runs as $o" }
        }
        if ($srv -and $srv.state -eq 'connected' -and $srv.account -and $owners.Count -gt 0 -and $owners -notcontains $srv.account) {
            $problems += "WRONG IDENTITY: the server has it connected as $($srv.account), the process belongs to $($owners -join ', ')"
        }
        if ($r.Autostart -eq 'NONE') { $problems += 'no autostart' }
        if ($users.Count -gt 0 -and $procs.Count -eq 0) { $problems += 'signed in but helper not running (starts at next sign-in)' }
        if ($procs.Count -gt 0 -and $srv -and $srv.state -ne 'connected') { $problems += 'running but not connected to the server' }
        $r.Problem = $problems -join '; '
    }
    catch { $r.Problem = "check failed: $($_.Exception.Message.Trim())" }
    [pscustomobject]$r
}

# every PC in parallel, each with a hard deadline, so one dead PC cannot stall the run
$pool = [runspacefactory]::CreateRunspacePool(1, 6); $pool.Open()
$jobs = foreach ($pc in $Computer) { $ps = [powershell]::Create().AddScript($check).AddArgument($pc).AddArgument($coverage); $ps.RunspacePool = $pool; [pscustomobject]@{ Pc = $pc; Ps = $ps; H = $ps.BeginInvoke() } }
$rows = foreach ($j in $jobs) {
    if ($j.H.AsyncWaitHandle.WaitOne(75000)) { try { $j.Ps.EndInvoke($j.H) } catch { [pscustomobject]@{ PC = $j.Pc; Online = $false; Problem = "check failed: $($_.Exception.Message)" } } }
    else { [pscustomobject]@{ PC = $j.Pc; Online = $false; Autostart = ''; Running = ''; Connected = ''; LastSeen = ''; RunsAs = ''; SignedIn = ''; Problem = 'no answer within 75 s (PC off or unreachable)' } }
}
$pool.Close()
$rows = @($rows)

if ($PassThru) { $rows; return }
$rows | Format-Table PC, Online, Autostart, Running, Connected, LastSeen, RunsAs, SignedIn -AutoSize | Out-String -Width 240 | Write-Host
if ($serverNote) { Write-Host $serverNote -ForegroundColor Yellow }
$bad = @($rows | Where-Object { $_.Problem })
foreach ($b in $bad) { Write-Host ("{0}: {1}" -f $b.PC, $b.Problem) -ForegroundColor $(if ($b.Problem -match 'WRONG IDENTITY') { 'Red' } else { 'Yellow' }) }
$wrong = @($rows | Where-Object { $_.Problem -match 'WRONG IDENTITY' }).Count
Write-Host ("{0} PC(s): {1} fully good, {2} with notes, {3} WRONG IDENTITY." -f $rows.Count, ($rows.Count - $bad.Count), $bad.Count, $wrong)
if ($wrong -gt 0) { exit 1 }
