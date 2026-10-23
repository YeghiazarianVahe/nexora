<#
  Nexora F5 collector: outlook-files
  OST/PST/NST files per user profile (file system + Outlook 16.0 profile registry in loaded hives).
  Read-only, Windows PowerShell 5.1, no modules. Emits exactly one JSON object on stdout.
  Schema: docs/diagnostics/collector-schemas.md
#>
[CmdletBinding()]
param([int]$BudgetSeconds = 25)
$CollectorId = 'outlook-files'
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

function Find-StorePaths([string]$root) {
    $out = New-Object System.Collections.Generic.List[string]
    if (-not (Test-Path -LiteralPath $root)) { return , @() }
    $rx = [regex]'(?i)(?:[A-Za-z]:\\|\\\\)[^\x00-\x1f"<>|*?]{1,240}?\.(?:ost|pst|nst)(?![A-Za-z0-9])'
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
function Scrub-Path([string]$p) {
    $p = $p -replace '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}(?=\.(?:ost|pst|nst)$)', '<email>'
    return ($p -replace '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}', '<email>')
}
$script:found = @{}
$script:remoteLetters = @{}
function Add-Found([string]$user, [string]$path, [string]$source, $remote) {
    $m = [regex]::Match($path, '\.(ost|pst|nst)$', [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $m.Success) { return }
    $kind = $m.Groups[1].Value.ToLowerInvariant()
    $letter = $null
    if ($path -match '^([A-Za-z]):\\') { $letter = $Matches[1].ToUpperInvariant() }
    $net = $path.StartsWith('\\')
    if ($null -ne $letter -and ($remote.ContainsKey($letter) -or $script:remoteLetters.ContainsKey($letter))) { $net = $true }
    $key = ($user + '|' + $path).ToLowerInvariant()
    if ($script:found.ContainsKey($key)) {
        $e = $script:found[$key]
        if ($e['source'] -ne $source) { $e['source'] = 'both' }
        return
    }
    $size = $null; $lw = $null; $exists = $null
    if (-not $net) {
        try {
            $fi = New-Object System.IO.FileInfo $path
            $exists = [bool]$fi.Exists
            if ($exists) { $size = [int64]$fi.Length; $lw = Iso $fi.LastWriteTime }
        } catch { Write-Verbose 'stat failed' }
    }
    $script:found[$key] = [ordered]@{
        user = $user; kind = $kind; path = (Cut (Scrub-Path $path)); sizeBytes = $size; lastWriteUtc = $lw
        onNetworkShare = [bool]$net; driveLetter = $letter; source = $source; exists = $exists
    }
}

$profiles = @()
try { $profiles = Get-UserProfiles } catch { Add-Err ('profiles: ' + $_.Exception.Message) }
try {
    foreach ($d in @(Get-CimInstance -ClassName Win32_LogicalDisk -Filter 'DriveType=4')) { $script:remoteLetters[([string]$d.DeviceID).Substring(0, 1).ToUpperInvariant()] = $true }
} catch { Add-Err ('drives: ' + $_.Exception.Message) }

foreach ($p in $profiles) {
    if (Over) { break }
    $remote = @{}
    if ($p.Loaded) {
        try { foreach ($k in @(Get-ChildItem -LiteralPath ('Registry::HKEY_USERS\' + $p.Sid + '\Network'))) { $remote[$k.PSChildName.ToUpperInvariant()] = $true } } catch { Write-Verbose 'no mapped drives' }
    }
    try {
        foreach ($dir in @((Join-Path $p.Path 'AppData\Local\Microsoft\Outlook'), (Join-Path $p.Path 'Documents\Outlook Files'))) {
            if (-not (Test-Path -LiteralPath $dir)) { continue }
            foreach ($f in @(Get-ChildItem -LiteralPath $dir -File -Force -ErrorAction SilentlyContinue)) {
                if ($f.Extension -match '^\.(ost|pst|nst)$') { Add-Found $p.Name $f.FullName 'filesystem' $remote }
            }
        }
    } catch { Add-Err ('fs: ' + $_.Exception.Message) }
    if ($p.Loaded) {
        try {
            $paths = Find-StorePaths ('Registry::HKEY_USERS\' + $p.Sid + '\Software\Microsoft\Office\16.0\Outlook\Profiles')
            foreach ($x in $paths) { Add-Found $p.Name $x 'profile' $remote }
        } catch { Add-Err ('profile: ' + $_.Exception.Message) }
    }
}
$list = Limit @($script:found.Values | Sort-Object -Property @{ Expression = { $_['sizeBytes'] }; Descending = $true }) 200
Emit ([ordered]@{ userScope = (Get-Scope $profiles); usersScanned = @($profiles).Count; files = $list })
