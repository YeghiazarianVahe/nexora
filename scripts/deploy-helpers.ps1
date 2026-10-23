<#
.SYNOPSIS
  Rolls the Nexora helper out to the Service Desk operator PCs from this PC, without visiting each one. No WinRM.

.DESCRIPTION
  For each PC (bounded parallelism) it uses the admin share and the Task Scheduler / WMI (RPC/DCOM) with YOUR administrator rights:
    1. downloads the helper from the Nexora server once (that copy carries the server's public key; a helper without it refuses every job),
    2. copies it to  C:\ProgramData\Nexora\Helper\  on the PC (read/execute for ordinary users, nobody but administrators can change it),
    3. registers ONE scheduled task, "Nexora Helper", at logon of ANY user, running as the built-in Users GROUP with limited rights.
       Task Scheduler then starts it in the logging-on person's own session with that person's own token. It never runs as you and never as SYSTEM.
       The task is written as XML (locale independent) and created-or-updated by name, so re-running updates it and never duplicates it.
    4. if someone is signed in, starts the helper for them now and reports who it runs as.
  If the task cannot be registered in that context the PC is reported as "needs per-user install"; there is no fallback to another identity.

  A PC with nobody signed in is not a failure: "task registered, will start at next logon".

.EXAMPLE
  .\scripts\deploy-helpers.ps1 -Computer wks-44 -WhatIf
.EXAMPLE
  .\scripts\deploy-helpers.ps1 -Computer wks-44
.EXAMPLE
  .\scripts\deploy-helpers.ps1                      # the 11 operator PCs
#>
[CmdletBinding()]
param(
    [string[]]$Computer = @('wks-50', 'wks-007', 'wks-36', 'wks-111', 'wks-44', 'wks-201', 'wks-122', 'wks-75', 'wks-59', 'wks-51', 'wks-25', 'wks-13'),

    # The Nexora server the helpers connect to, and where the stamped helper is downloaded from.
    [string]$Server = 'http://wks-111:5090',

    # Use this helper file instead of downloading one (it must already carry the server key, i.e. come from the Nexora download).
    [string]$HelperPath,

    [ValidateRange(1, 8)][int]$ThrottleLimit = 4,

    # Show what would be done; change nothing.
    [switch]$WhatIf,

    # Remove the autostart (task and Startup shortcut) and the helper file from the PCs.
    [switch]$Uninstall,

    # After installing, wait up to this many seconds for each running helper to show as connected on the Nexora server (needs an Admin role there).
    [ValidateRange(0, 300)][int]$WaitForConnectedSeconds = 0,

    [switch]$PassThru
)

$ErrorActionPreference = 'Stop'
# "-File x.ps1 -Computer a,b,c" arrives as the single string "a,b,c": split it.
$Computer = @($Computer | ForEach-Object { $_ -split '[,;\s]+' } | Where-Object { $_ })
$TaskName = 'Nexora Helper'

# ---- one deployment at a time on this PC
$createdNew = $false
$mutex = New-Object System.Threading.Mutex($false, 'Global\NexoraDeployHelpers', [ref]$createdNew)
if (-not $mutex.WaitOne(0)) { Write-Host '[FAIL] Another deploy-helpers.ps1 is already running on this PC.' -ForegroundColor Red; exit 2 }

try {
    # ---- the helper to distribute
    $tempFile = $null
    if (-not $HelperPath -and -not $Uninstall) {
        $tempFile = Join-Path ([IO.Path]::GetTempPath()) ('Nexora.Helper.' + [guid]::NewGuid().ToString('N') + '.exe')
        Write-Host "Downloading the helper from $Server ..."
        Invoke-WebRequest -Uri "$Server/api/helper/download" -OutFile $tempFile -UseDefaultCredentials -UseBasicParsing
        $HelperPath = $tempFile
    }
    if (-not $Uninstall -and -not (Test-Path -LiteralPath $HelperPath)) { throw "Helper not found: $HelperPath" }

    $localHash = $null
    if (-not $Uninstall) {
        # The server key is stamped at the end of the file; refuse to distribute a helper without it.
        $fs = [IO.File]::OpenRead($HelperPath)
        try {
            $tail = New-Object byte[] 8
            [void]$fs.Seek(-8, [IO.SeekOrigin]::End)
            [void]$fs.Read($tail, 0, 8)
        }
        finally { $fs.Dispose() }
        if ([Text.Encoding]::ASCII.GetString($tail) -ne 'NXTRUST1') { throw "$HelperPath carries no server key; it would refuse every job. Download it from $Server/api/helper/download." }
        $localHash = (Get-FileHash -LiteralPath $HelperPath -Algorithm SHA256).Hash
        Write-Host ("Helper {0}  sha256 {1}..." -f $HelperPath, $localHash.Substring(0, 12))
    }

    # The scheduled task as XML: any user's logon, run as the Users GROUP (the person who logs on), limited rights, one instance.
    $taskXmlTemplate = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>Nexora helper: runs your remote actions as you. Starts at your sign-in, in your session.</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>
  <Principals><Principal id="Author"><GroupId>S-1-5-32-545</GroupId><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <StartWhenAvailable>true</StartWhenAvailable>
    <Hidden>true</Hidden>
  </Settings>
  <Actions Context="Author"><Exec><Command>{EXE}</Command><Arguments>--server {SERVER} --hidden</Arguments><WorkingDirectory>{DIR}</WorkingDirectory></Exec></Actions>
</Task>
"@

    # ---- the work for ONE PC (runs in a runspace)
    $worker = {
        param($pc, $helperPath, $localHash, $taskName, $taskXmlTemplate, $whatIf, $server, $uninstall)
        $r = [ordered]@{ Computer = $pc; Reachable = $false; Files = ''; Task = ''; User = ''; Running = ''; RunsAs = ''; Result = ''; Detail = '' }
        try {
            # The PC's real system drive (WMI over DCOM, never WinRM); only if that cannot be read is C: assumed, and the result says so.
            $sysDrive = $null
            try {
                $cim0 = New-CimSession -ComputerName $pc -SessionOption (New-CimSessionOption -Protocol Dcom) -OperationTimeoutSec 20 -ErrorAction Stop
                $sysDrive = (Get-CimInstance -CimSession $cim0 -ClassName Win32_OperatingSystem -Property SystemDrive -ErrorAction Stop).SystemDrive
                Remove-CimSession $cim0
            }
            catch { }
            if ($sysDrive -notmatch '^[A-Za-z]:$') { $sysDrive = 'C:'; $r.Detail += 'System drive not readable; assumed C:. ' }
            $letter = $sysDrive.Substring(0, 1)
            $remoteDirLocal = "$sysDrive\ProgramData\Nexora\Helper"
            $remoteExeLocal = "$remoteDirLocal\Nexora.Helper.exe"
            $taskXml = $taskXmlTemplate.Replace('{EXE}', [Security.SecurityElement]::Escape($remoteExeLocal)).Replace('{DIR}', [Security.SecurityElement]::Escape($remoteDirLocal)).Replace('{SERVER}', [Security.SecurityElement]::Escape($server))

            $root = "\\$pc\$letter`$"
            if (-not (Test-Path -LiteralPath $root)) { $r.Result = 'Unreachable'; $r.Detail += "Cannot open $root with your rights (PC off, wrong name, or you are not an administrator there)."; return [pscustomobject]$r }
            $r.Reachable = $true
            $dirUnc = Join-Path $root 'ProgramData\Nexora\Helper'
            $exeUnc = Join-Path $dirUnc 'Nexora.Helper.exe'
            $startupUnc = Join-Path $root 'ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp'
            $lnkUnc = Join-Path $startupUnc 'Nexora Helper.lnk'

            # who is signed in (RPC; no WinRM)
            $sessions = @()
            $sessionIds = @()
            try {
                $q = & quser.exe /server:$pc 2>$null
                if ($LASTEXITCODE -eq 0 -and $q) {
                    foreach ($line in ($q | Select-Object -Skip 1)) {
                        $columns = ($line -replace '^[>\s]+', '') -split '\s{2,}'
                        $sessions += $columns[0]
                        if ($line -match '^\s*>?.+?\s+(\d+)\s+\S+\s+') { $sessionIds += [int]$Matches[1] }
                    }
                }
            }
            catch { }
            $r.User = ($sessions | Where-Object { $_ } | Select-Object -Unique) -join ', '

            if ($whatIf) {
                $r.Result = 'WhatIf'
                $r.Detail += if ($uninstall) { "Would remove task '$taskName', $lnkUnc and $exeUnc." } else { "Would copy to $exeUnc and set up the autostart (Users group, at logon; else the Startup shortcut)." }
                return [pscustomobject]$r
            }

            # ---- uninstall
            if ($uninstall) {
                $removed = @()
                try { $svcU = New-Object -ComObject 'Schedule.Service'; $svcU.Connect($pc); $svcU.GetFolder('\').DeleteTask($taskName, 0); $removed += 'logon task' } catch { }
                if (Test-Path -LiteralPath $lnkUnc) { Remove-Item -LiteralPath $lnkUnc -Force; $removed += 'startup shortcut' }
                if (Test-Path -LiteralPath $exeUnc) {
                    try { Remove-Item -LiteralPath $exeUnc -Force; $removed += 'helper file' }
                    catch { Move-Item -LiteralPath $exeUnc -Destination "$exeUnc.old-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))" -Force; $removed += 'helper file (running copy renamed aside)' }
                }
                $r.Task = $removed -join ', '
                $r.Result = if ($removed) { 'OK: removed' } else { 'OK: nothing was installed' }
                return [pscustomobject]$r
            }

            # The server operator may already use the published helper via HKCU.
            # Keep a verified, current per-user installation rather than requiring
            # a second installation in the local administrator-protected directory.
            if (($pc -split '\.')[0] -ieq $env:COMPUTERNAME) {
                $ownRun = Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name NexoraHelper -ErrorAction SilentlyContinue
                if ($ownRun -and $ownRun.NexoraHelper -match '^"([^"]+)"\s+--server\s+(\S+)\s+--hidden$') {
                    $ownExe = $Matches[1]
                    $ownServer = $Matches[2]
                    if ($ownServer -eq $server -and (Test-Path -LiteralPath $ownExe) -and (Get-FileHash -LiteralPath $ownExe -Algorithm SHA256).Hash -eq $localHash) {
                        $ownSession = (Get-Process -Id $PID).SessionId
                        $ownProcesses = @(Get-Process -Name Nexora.Helper -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $ownSession -and $_.Path -eq $ownExe })
                        if ($ownProcesses.Count -eq 0) { Start-Process -FilePath $ownExe -ArgumentList '--server', $server, '--hidden' -WindowStyle Hidden; Start-Sleep -Seconds 2 }
                        $ownProcesses = @(Get-Process -Name Nexora.Helper -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $ownSession -and $_.Path -eq $ownExe })
                        $r.Files = 'current'; $r.Task = 'per-user Run key'
                        $r.Running = if ($ownProcesses.Count) { "yes ($($ownProcesses.Count))" } else { 'no' }
                        $r.RunsAs = [Security.Principal.WindowsIdentity]::GetCurrent().Name
                        $r.Result = if ($ownProcesses.Count) { 'OK' } else { 'Needs attention: per-user helper did not start' }
                        return [pscustomobject]$r
                    }
                }
            }

            # 1. files
            New-Item -ItemType Directory -Force -Path $dirUnc | Out-Null
            & icacls.exe $dirUnc /inheritance:r /grant:r 'NT AUTHORITY\SYSTEM:(OI)(CI)F' 'BUILTIN\Administrators:(OI)(CI)F' 'BUILTIN\Users:(OI)(CI)RX' | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Could not restrict permissions on $dirUnc (icacls exit $LASTEXITCODE); nothing further was done." }
            $same = (Test-Path -LiteralPath $exeUnc) -and ((Get-FileHash -LiteralPath $exeUnc -Algorithm SHA256).Hash -eq $localHash)
            if ($same) { $r.Files = 'current' }
            else {
                try { Copy-Item -LiteralPath $helperPath -Destination $exeUnc -Force; $r.Files = 'copied' }
                catch [System.IO.IOException] {
                    # A running program cannot be overwritten but can be renamed: move the running file aside, put the new one in place.
                    # The running helper keeps working from the renamed file; the next start uses the new one.
                    $aside = "$exeUnc.old-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))"
                    Move-Item -LiteralPath $exeUnc -Destination $aside -Force
                    Copy-Item -LiteralPath $helperPath -Destination $exeUnc -Force
                    $r.Files = 'updated (the running copy was renamed aside; the new one starts at the next sign-in)'
                }
            }
            # remove older renamed copies that are no longer running
            Get-ChildItem -LiteralPath $dirUnc -Filter 'Nexora.Helper.exe.old-*' -ErrorAction SilentlyContinue | ForEach-Object { try { Remove-Item -LiteralPath $_.FullName -Force } catch { } }

            # 2. autostart at every sign-in, running as whoever signs in. Preferred: one logon task for the Users group. Task Scheduler refuses
            #    group-principal tasks on some PCs when created remotely (0x80070032); then the all-users Startup shortcut does the same job
            #    (it also runs in the signing-in person's own session with their own token). Neither ever runs as you or as SYSTEM.
            #    The task is XML (no dates, no language-dependent text), so it is the same on a PC with any regional settings.
            $autostart = ''
            try {
                $svc = New-Object -ComObject 'Schedule.Service'
                $svc.Connect($pc)
                [void]$svc.GetFolder('\').RegisterTask($taskName, $taskXml, 6, $null, $null, 4)   # CREATE_OR_UPDATE, logon type GROUP; the XML carries the principal
                $autostart = 'logon task (Users group)'
                # one autostart only: drop an older Startup shortcut so the helper is not started twice at logon
                if (Test-Path -LiteralPath $lnkUnc) { Remove-Item -LiteralPath $lnkUnc -Force; $autostart += '; older Startup shortcut removed' }
            }
            catch {
                $why = $_.Exception.Message.Trim()
                try {
                    New-Item -ItemType Directory -Force -Path $startupUnc | Out-Null
                    $shell = New-Object -ComObject WScript.Shell
                    $lnk = $shell.CreateShortcut($lnkUnc)
                    $lnk.TargetPath = $remoteExeLocal
                    $lnk.Arguments = "--server $server --hidden"
                    $lnk.WorkingDirectory = $remoteDirLocal
                    $lnk.WindowStyle = 7
                    $lnk.Description = 'Nexora helper: runs your remote actions as you'
                    $lnk.Save()
                    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
                    $autostart = 'all-users Startup shortcut (remote logon task not supported here)'
                    try { $svcOld = New-Object -ComObject 'Schedule.Service'; $svcOld.Connect($pc); $svcOld.GetFolder('\').DeleteTask($taskName, 0) } catch { }
                }
                catch {
                    $r.Task = 'NOT registered'
                    $r.Result = 'Needs per-user install'
                    $r.Detail += "Autostart could not be set up in the users' context (task: $why; shortcut: $($_.Exception.Message.Trim())). Not falling back to any other identity."
                    return [pscustomobject]$r
                }
            }
            $r.Task = $autostart

            # RunEx with TASK_RUN_USE_SESSION_ID starts as the person in that session,
            # without credentials or a SYSTEM process. The helper's session mutex rejects duplicates.
            if ($autostart -like 'logon task*') {
                foreach ($sessionId in ($sessionIds | Select-Object -Unique)) {
                    if ($sessionId -le 0) { continue }
                    try { [void]$svc.GetFolder('\').GetTask($taskName).RunEx($null, 4, $sessionId, $null) }
                    catch { $r.Detail += "Could not start session $sessionId now: $($_.Exception.Message.Trim()). " }
                }
            }
            # 3. Verify the process and its owner (WMI over DCOM, never WinRM).
            if ($r.User) {
                Start-Sleep -Seconds 4
                try {
                    $cim = New-CimSession -ComputerName $pc -SessionOption (New-CimSessionOption -Protocol Dcom) -OperationTimeoutSec 20 -ErrorAction Stop
                    $procs = @(Get-CimInstance -CimSession $cim -ClassName Win32_Process -Filter "Name='Nexora.Helper.exe'" -ErrorAction Stop)
                    $owners = @($procs | ForEach-Object { $o = Invoke-CimMethod -CimSession $cim -InputObject $_ -MethodName GetOwner; "$($o.Domain)\$($o.User)" } | Select-Object -Unique)
                    $r.Running = if ($procs.Count -gt 0) { "yes ($($procs.Count))" } else { 'no' }
                    $r.RunsAs = $owners -join ', '
                    Remove-CimSession $cim
                }
                catch { $r.Running = 'unknown'; $r.Detail += "Could not read processes ($($_.Exception.Message.Trim()))." }
            }
            else { $r.Running = 'nobody signed in' }

            $r.Result = if ($r.User) { if ($r.Running -like 'yes*') { 'OK' } else { 'Needs attention: autostart installed but helper did not start; inspect Windows and helper logs' } } else { 'OK: autostart installed, will start at next logon' }
        }
        catch {
            $r.Result = 'Failed'; $r.Detail += $_.Exception.Message
        }
        [pscustomobject]$r
    }

    # ---- bounded parallelism (Windows PowerShell 5.1 compatible runspace pool)
    $pool = [runspacefactory]::CreateRunspacePool(1, $ThrottleLimit)
    $pool.Open()
    $jobs = foreach ($pc in $Computer) {
        $ps = [powershell]::Create().AddScript($worker).AddArgument($pc.Trim()).AddArgument($HelperPath).AddArgument($localHash).AddArgument($TaskName).AddArgument($taskXmlTemplate).AddArgument([bool]$WhatIf).AddArgument($Server).AddArgument([bool]$Uninstall)
        $ps.RunspacePool = $pool
        [pscustomobject]@{ Pc = $pc; Ps = $ps; Handle = $ps.BeginInvoke() }
    }
    $results = foreach ($j in $jobs) {
        try { $j.Ps.EndInvoke($j.Handle) } catch { [pscustomobject]@{ Computer = $j.Pc; Result = 'Failed'; Detail = $_.Exception.Message } }
        $j.Ps.Dispose()
    }
    $pool.Close()
    $results = @($results)

    # Optionally wait until each helper that is running shows as connected on the Nexora server (the same list as the Admin page "Helpers").
    foreach ($x in $results) { Add-Member -InputObject $x -NotePropertyName Connected -NotePropertyValue 'unchecked' -Force }
    if ($WaitForConnectedSeconds -gt 0 -and -not $Uninstall -and -not $WhatIf) {
        $deadline = (Get-Date).AddSeconds($WaitForConnectedSeconds)
        $waiting = @($results | Where-Object { $_.Running -like 'yes*' })
        foreach ($x in $waiting) { $x.Connected = 'no' }
        while ($waiting.Count -gt 0 -and (Get-Date) -lt $deadline) {
            try {
                $cov = Invoke-RestMethod -Uri "$Server/api/helper-coverage" -UseDefaultCredentials -UseBasicParsing
                foreach ($x in $waiting) {
                    $row = $cov.pcs | Where-Object { $_.pc -eq $x.Computer.ToLowerInvariant() }
                    if ($row -and $row.state -eq 'connected') { $x.Connected = "yes ($($row.account))" }
                }
            }
            catch { foreach ($x in $waiting) { $x.Connected = 'unchecked (Nexora coverage list not readable)' }; break }
            $waiting = @($waiting | Where-Object { $_.Connected -eq 'no' })
            if ($waiting.Count -gt 0) { Start-Sleep -Seconds 3 }
        }
    }

    if ($PassThru) { $results }
    else {
        $results | Format-Table Computer, Reachable, Files, Task, User, Running, RunsAs, Connected, Result -Wrap -AutoSize | Out-String -Width 220 | Write-Host
        foreach ($x in $results | Where-Object { $_.Detail }) { Write-Host ("{0}: {1}" -f $x.Computer, $x.Detail) }
        $bad = @($results | Where-Object { $_.Result -notmatch '^(OK|WhatIf)' }).Count
        Write-Host ("{0} PC(s): {1} fine, {2} need attention. Run scripts\verify-helpers.ps1 to confirm they are connected." -f @($results).Count, (@($results).Count - $bad), $bad)
    }
}
finally {
    if ($tempFile -and (Test-Path -LiteralPath $tempFile)) { Remove-Item -LiteralPath $tempFile -Force -ErrorAction SilentlyContinue }
    $mutex.ReleaseMutex(); $mutex.Dispose()
}
