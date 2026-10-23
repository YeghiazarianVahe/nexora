<#
  Nexora F5 collector: outlook-profile
  Outlook profile cached-mode facts from loaded user hives (best effort, null = unknown).
  Read-only, Windows PowerShell 5.1, no modules. Emits exactly one JSON object on stdout.
  Schema: docs/diagnostics/collector-schemas.md
#>
[CmdletBinding()]
param([int]$BudgetSeconds = 25)
$CollectorId = 'outlook-profile'
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

function Find-OstPaths([string]$root) {
    $out = New-Object System.Collections.Generic.List[string]
    if (-not (Test-Path -LiteralPath $root)) { return , @() }
    $rx = [regex]'(?i)(?:[A-Za-z]:\\|\\\\)[^\x00-\x1f"<>|*?]{1,240}?\.ost(?![A-Za-z0-9])'
    $stack = New-Object System.Collections.Generic.Stack[object]
    $stack.Push(@($root, 0))
    $visited = 0
    while ($stack.Count -gt 0 -and $visited -lt 1500) {
        if (Over) { break }
        $item = $stack.Pop(); $visited++
        try {
            $key = Get-Item -LiteralPath $item[0]
            foreach ($n in $key.GetValueNames()) {
                $v = $key.GetValue($n)
                if ($v -is [byte[]] -and $v.Length -ge 8 -and $v.Length -le 4096) {
                    foreach ($txt in @([Text.Encoding]::Unicode.GetString($v), [Text.Encoding]::ASCII.GetString($v))) {
                        foreach ($m in $rx.Matches($txt)) { if (-not $out.Contains($m.Value)) { $out.Add($m.Value) } }
                    }
                }
            }
            if ([int]$item[1] -lt 4) {
                foreach ($s in $key.GetSubKeyNames()) { $stack.Push(@(($item[0] + '\' + $s), ([int]$item[1] + 1))) }
            }
        } catch { Write-Verbose 'registry key skipped' }
    }
    return , $out.ToArray()
}
function To-Int($v) { if ($v -is [int]) { return [int]$v }; return $null }

$profiles = @()
try { $profiles = Get-UserProfiles } catch { Add-Err ('profiles: ' + $_.Exception.Message) }
$per = New-Object System.Collections.Generic.List[object]
foreach ($p in $profiles) {
    if (-not $p.Loaded) { continue }
    if (Over) { break }
    try {
        $root = 'Registry::HKEY_USERS\' + $p.Sid + '\Software'
        $ol = $root + '\Microsoft\Office\16.0\Outlook'
        $prof = $ol + '\Profiles'
        $count = 0
        if (Test-Path -LiteralPath $prof) { $count = @(Get-ChildItem -LiteralPath $prof).Count }
        $def = [string](Get-Val $ol 'DefaultProfile')
        $ost = $null; $cached = $null
        if ($count -gt 0 -and $def -and (Test-Path -LiteralPath ($prof + '\' + $def))) {
            $paths = Find-OstPaths ($prof + '\' + $def)
            $ost = @($paths).Count
            if ($ost -gt 0) { $cached = $true }
        }
        $polCm = $root + '\Policies\Microsoft\Office\16.0\Outlook\Cached Mode'
        $usrCm = $ol + '\Cached Mode'
        $en = To-Int (Get-Val $polCm 'Enable')
        if ($en -eq 1) { $cached = $true } elseif ($en -eq 0) { $cached = $false }
        $months = To-Int (Get-Val $polCm 'SyncWindowSetting')
        if ($null -eq $months) { $months = To-Int (Get-Val $usrCm 'SyncWindowSetting') }
        $dsf = To-Int (Get-Val $polCm 'DownloadSharedFolders')
        if ($null -eq $dsf) { $dsf = To-Int (Get-Val $usrCm 'DownloadSharedFolders') }
        $dsfb = $null
        if ($null -ne $dsf) { $dsfb = ($dsf -ne 0) }
        $per.Add([ordered]@{ user = $p.Name; profileCount = $count; cachedMode = $cached; ostCount = $ost; exchangeCachedModeMonths = $months; downloadSharedFolders = $dsfb })
    } catch { Add-Err ('profile: ' + $_.Exception.Message) }
}
$primary = $null
foreach ($e in $per) { if ($e['profileCount'] -gt 0) { $primary = $e; break } }
if ($null -eq $primary -and $per.Count -gt 0) { $primary = $per[0] }
$summary = [ordered]@{ count = $null; cachedMode = $null; downloadSharedFolders = $null; sharedMailboxesCached = $null; additionalMailboxes = $null; ostCount = $null; exchangeCachedModeMonths = $null }
if ($null -ne $primary) {
    $summary.count = $primary['profileCount']; $summary.cachedMode = $primary['cachedMode']; $summary.downloadSharedFolders = $primary['downloadSharedFolders']
    $summary.ostCount = $primary['ostCount']; $summary.exchangeCachedModeMonths = $primary['exchangeCachedModeMonths']
}
$pu = Limit @($per | ForEach-Object { [ordered]@{ user = $_['user']; profileCount = $_['profileCount']; cachedMode = $_['cachedMode']; ostCount = $_['ostCount']; exchangeCachedModeMonths = $_['exchangeCachedModeMonths'] } }) 50
Emit ([ordered]@{ userScope = (Get-Scope $profiles); profiles = $summary; perUser = $pu })
