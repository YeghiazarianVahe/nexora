<#
  Nexora F5 collector: pc-slow-startup
  Startup items (Run keys, Startup folders), stopped auto services, logon tasks count, uptime.
  Read-only, Windows PowerShell 5.1, no modules. Emits exactly one JSON object on stdout.
  Schema: docs/diagnostics/collector-schemas.md
#>
[CmdletBinding()]
param([int]$BudgetSeconds = 25)
$CollectorId = 'pc-slow-startup'
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Threading.Thread]::CurrentThread.CurrentCulture = [cultureinfo]::InvariantCulture
$sw = [Diagnostics.Stopwatch]::StartNew()
$script:errs = New-Object System.Collections.Generic.List[string]
$script:incomplete = $false
$script:reason = $null
$script:truncated = $false

function Cut([string]$s, [int]$n = 260) {
    if ($null -eq $s) { return $null }
    if ($s.Length -gt $n) { $script:truncated = $true; return $s.Substring(0, $n) }
    return $s
}
function Add-Err([string]$m) { if ($script:errs.Count -lt 20) { $script:errs.Add((Cut $m 160)) } }
function Iso($d) {
    if ($null -eq $d) { return $null }
    return ([datetime]$d).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [cultureinfo]::InvariantCulture)
}
function Over {
    if ($sw.Elapsed.TotalSeconds -ge $BudgetSeconds) { $script:incomplete = $true; $script:reason = 'time budget'; return $true }
    return $false
}
function Limit($a, [int]$n = 200) {
    $a = @($a)
    if ($a.Count -gt $n) { $script:truncated = $true; return , @($a[0..($n - 1)]) }
    return , $a
}
function Shrink($h) {
    foreach ($k in @($h.Keys)) {
        $v = $h[$k]
        if ($v -is [array]) { if ($v.Count -gt 1) { $h[$k] = @($v[0..([int][math]::Floor($v.Count / 2) - 1)]) } }
        elseif ($v -is [System.Collections.IDictionary]) { Shrink $v }
    }
}
function Emit($body) {
    $o = [ordered]@{
        schema = 1; collector = $CollectorId; collectedUtc = (Iso (Get-Date))
        incomplete = $script:incomplete; incompleteReason = $script:reason
        errors = @($script:errs); truncated = $script:truncated; elapsedMs = [int]$sw.ElapsedMilliseconds
    }
    foreach ($k in $body.Keys) { $o[$k] = $body[$k] }
    $json = ConvertTo-Json -InputObject $o -Depth 6 -Compress
    $i = 0
    while ($json.Length -gt 20000 -and $i -lt 12) {
        $o['truncated'] = $true; Shrink $o
        $json = ConvertTo-Json -InputObject $o -Depth 6 -Compress; $i++
    }
    Write-Output $json
}
function Get-Val([string]$path, [string]$name) {
    try { return (Get-ItemProperty -LiteralPath $path -Name $name -ErrorAction Stop).$name } catch { return $null }
}

function Get-UserProfiles {
    $res = New-Object System.Collections.Generic.List[object]
    $pl = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList'
    $usersRoot = (Join-Path $env:SystemDrive 'Users')
    foreach ($k in Get-ChildItem -LiteralPath $pl) {
        $sid = $k.PSChildName
        if ($sid -notmatch '^S-1-5-21-') { continue }
        $p = [string](Get-Val $k.PSPath 'ProfileImagePath')
        if ([string]::IsNullOrEmpty($p)) { continue }
        if (-not $p.StartsWith($usersRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { continue }
        $leaf = Split-Path -Path $p -Leaf
        if ($leaf -notmatch '^[\x20-\x7E]+$') { continue }
        if ($leaf -match '^(Default|Public|All Users)' -or $leaf -match '^(defaultuser0|WDAGUtilityAccount)$') { continue }
        $res.Add([pscustomobject]@{ Sid = $sid; Name = $leaf; Path = $p; Loaded = (Test-Path -LiteralPath ('Registry::HKEY_USERS\' + $sid)) })
    }
    return , $res.ToArray()
}
function Get-Scope($profiles) {
    $p = @($profiles)
    if ($p.Count -eq 0) { return 'none' }
    $l = @($p | Where-Object { $_.Loaded }).Count
    if ($l -eq 0) { return 'none' }
    if ($l -eq $p.Count) { return 'full' }
    return 'partial'
}

function Get-CmdPath([string]$c) {
    $c = $c.Trim()
    if ($c.StartsWith('"')) {
        $e = $c.IndexOf('"', 1)
        if ($e -gt 0) { return $c.Substring(1, $e - 1) }
        return $c.Trim('"')
    }
    if ($c -match '^(.*?\.(exe|bat|cmd|com|lnk|vbs|ps1|js|msc|cpl))(\s|$)') { return $Matches[1] }
    return ($c -split '\s+')[0]
}
function Read-Run([string]$path, [string]$scope, [string]$user) {
    if (-not (Test-Path -LiteralPath $path)) { return }
    $key = Get-Item -LiteralPath $path
    foreach ($n in $key.GetValueNames()) {
        $script:runTotal++
        $v = [string]$key.GetValue($n)
        $o = [ordered]@{ name = (Cut $n 100); command = (Cut (Get-CmdPath $v) 260); scope = $scope }
        if ($user) { $o['user'] = $user }
        $script:runKeys.Add($o)
    }
}
function Read-Startup([string]$dir, [string]$scope, [string]$user) {
    if (-not (Test-Path -LiteralPath $dir)) { return }
    foreach ($f in @(Get-ChildItem -LiteralPath $dir -Force -ErrorAction SilentlyContinue)) {
        if ($f.Name -eq 'desktop.ini') { continue }
        $script:folderTotal++
        $o = [ordered]@{ name = (Cut $f.Name 100); scope = $scope }
        if ($user) { $o['user'] = $user }
        $script:folderItems.Add($o)
    }
}
$script:runKeys = New-Object System.Collections.Generic.List[object]
$script:folderItems = New-Object System.Collections.Generic.List[object]
$script:runTotal = 0
$script:folderTotal = 0
$profiles = @()
try { $profiles = Get-UserProfiles } catch { Add-Err ('profiles: ' + $_.Exception.Message) }
$run = '\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
try {
    Read-Run ('HKLM:' + $run) 'machine' $null
    Read-Run ('HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run') 'machine' $null
    foreach ($p in $profiles) { if ($p.Loaded) { Read-Run ('Registry::HKEY_USERS\' + $p.Sid + '\Software\Microsoft\Windows\CurrentVersion\Run') 'user' $p.Name } }
} catch { Add-Err ('run: ' + $_.Exception.Message) }
try {
    Read-Startup (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Startup') 'machine' $null
    foreach ($p in $profiles) {
        if (Over) { break }
        try { Read-Startup (Join-Path $p.Path 'AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup') 'user' $p.Name } catch { Write-Verbose 'startup folder not readable' }
    }
} catch { Add-Err ('startup folders: ' + $_.Exception.Message) }

$autoStopped = 0
try { $autoStopped = @(Get-CimInstance -ClassName Win32_Service -Filter "StartMode='Auto' AND State<>'Running'").Count } catch { Add-Err ('services: ' + $_.Exception.Message) }

$logon = $null
if (-not (Over)) {
    try {
        $n = 0
        foreach ($t in @(Get-ScheduledTask)) {
            if ($t.State -eq 'Disabled') { continue }
            foreach ($tr in @($t.Triggers)) {
                if ($null -ne $tr -and $tr.CimClass.CimClassName -eq 'MSFT_TaskLogonTrigger') { $n++; break }
            }
        }
        $logon = $n
    } catch { Add-Err ('tasks: ' + $_.Exception.Message) }
}

$uptime = [ordered]@{ lastBootUtc = $null; days = $null }
try {
    $boot = (Get-CimInstance -ClassName Win32_OperatingSystem).LastBootUpTime
    $uptime.lastBootUtc = Iso $boot
    $uptime.days = [math]::Round(((Get-Date) - $boot).TotalDays, 1)
} catch { Add-Err ('uptime: ' + $_.Exception.Message) }

$rk = Limit $script:runKeys.ToArray() 200
$fi = Limit $script:folderItems.ToArray() 200
Emit ([ordered]@{
    userScope = (Get-Scope $profiles); runKeys = $rk; startupFolderItems = $fi
    runKeysTotal = $script:runTotal; startupFolderTotal = $script:folderTotal
    autoServicesStopped = $autoStopped; logonTasksCount = $logon; uptime = $uptime
})
