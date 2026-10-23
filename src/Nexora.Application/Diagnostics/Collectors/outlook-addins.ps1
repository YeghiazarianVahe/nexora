<#
  Nexora F5 collector: outlook-addins
  Outlook COM add-ins (machine + loaded user hives) and Outlook resiliency records.
  Read-only, Windows PowerShell 5.1, no modules. Emits exactly one JSON object on stdout.
  Schema: docs/diagnostics/collector-schemas.md
#>
[CmdletBinding()]
param([int]$BudgetSeconds = 25)
$CollectorId = 'outlook-addins'
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

$script:addins = New-Object System.Collections.Generic.List[object]
function Read-Addins([string]$root, [string]$scope, [string]$user, [bool]$wow) {
    if (-not (Test-Path -LiteralPath $root)) { return }
    foreach ($k in @(Get-ChildItem -LiteralPath $root)) {
        $desc = $k.GetValue('Description')
        if ([string]::IsNullOrEmpty([string]$desc)) { $desc = $k.GetValue('FriendlyName') }
        $lb = $k.GetValue('LoadBehavior')
        $lbv = $null
        if ($lb -is [int]) { $lbv = [int]$lb }
        $o = [ordered]@{ progId = (Cut $k.PSChildName 100); description = (Cut ([string]$desc) 120); loadBehavior = $lbv }
        $man = [string]$k.GetValue('Manifest')
        if ($man) { $o['manifestHint'] = (Cut $man 200) }
        $o['scope'] = $scope
        if ($user) { $o['user'] = $user }
        $o['wow6432'] = $wow
        $script:addins.Add($o)
    }
}
function Clean([string]$m) {
    $m = $m -replace '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}', '<email>'
    $m = $m -replace '(?i)([A-Z]:\\Users\\)[^\\]+', '$1<user>'
    return (Cut $m 120)
}
function To-Ms($v) {
    if ($v -is [int] -and $v -gt 0) { return [int]$v }
    if ($v -is [byte[]] -and $v.Length -eq 4) { $i = [BitConverter]::ToInt32($v, 0); if ($i -gt 0) { return $i } }
    return $null
}
$profiles = @()
try { $profiles = Get-UserProfiles } catch { Add-Err ('profiles: ' + $_.Exception.Message) }
try {
    Read-Addins 'HKLM:\SOFTWARE\Microsoft\Office\Outlook\Addins' 'machine' $null $false
    Read-Addins 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Office\Outlook\Addins' 'machine' $null $true
} catch { Add-Err ('machine addins: ' + $_.Exception.Message) }

$disabled = New-Object System.Collections.Generic.List[object]
$slow = New-Object System.Collections.Generic.List[object]
foreach ($p in $profiles) {
    if (-not $p.Loaded) { continue }
    if (Over) { break }
    $hku = 'Registry::HKEY_USERS\' + $p.Sid + '\Software\Microsoft\Office'
    try { Read-Addins ($hku + '\Outlook\Addins') 'user' $p.Name $false } catch { Add-Err ('user addins: ' + $_.Exception.Message) }
    try {
        $res = $hku + '\16.0\Outlook\Resiliency'
        if (Test-Path -LiteralPath ($res + '\DisabledItems')) {
            $dk = Get-Item -LiteralPath ($res + '\DisabledItems')
            foreach ($n in $dk.GetValueNames()) {
                $v = $dk.GetValue($n)
                if ($v -is [byte[]]) {
                    $runs = @([regex]::Matches([Text.Encoding]::Unicode.GetString($v), '[\x20-\x7E]{4,}') | ForEach-Object { $_.Value } | Select-Object -First 2)
                    if ($runs.Count -gt 0) { $disabled.Add([ordered]@{ name = (Clean ($runs -join ' | ')); user = $p.Name }) }
                }
            }
        }
        if (Test-Path -LiteralPath ($res + '\SlowAddinList')) {
            $sk = Get-Item -LiteralPath ($res + '\SlowAddinList')
            $times = $null
            if (Test-Path -LiteralPath ($res + '\AddinLoadTimes')) { $times = Get-Item -LiteralPath ($res + '\AddinLoadTimes') }
            foreach ($n in $sk.GetValueNames()) {
                $ms = To-Ms ($sk.GetValue($n))
                if ($null -eq $ms -and $null -ne $times) { $ms = To-Ms ($times.GetValue($n)) }
                $slow.Add([ordered]@{ progId = (Cut $n 100); durationMs = $ms; user = $p.Name })
            }
        }
    } catch { Add-Err ('resiliency: ' + $_.Exception.Message) }
}
$a = Limit $script:addins.ToArray() 200
$d = Limit $disabled.ToArray() 50
$s = Limit $slow.ToArray() 50
Emit ([ordered]@{ userScope = (Get-Scope $profiles); addins = $a; resiliency = [ordered]@{ disabledItems = $d; slowAddins = $s } })
