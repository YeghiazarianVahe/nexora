<#
  Nexora F5 collector: outlook-events
  Application log (last 7 days): Outlook crashes/hangs, slow add-in and .NET/Office events.
  Read-only, Windows PowerShell 5.1, no modules. Emits exactly one JSON object on stdout.
  Schema: docs/diagnostics/collector-schemas.md
#>
[CmdletBinding()]
param([int]$BudgetSeconds = 25)
$CollectorId = 'outlook-events'
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

function Clean([string]$m) {
    if ([string]::IsNullOrEmpty($m)) { return '' }
    $m = ($m -split "\r?\n")[0]
    $m = $m -replace '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}', '<email>'
    $m = $m -replace 'S-1-5-21(-\d+){3,5}', '<sid>'
    $m = $m -replace '(?i)([A-Z]:\\Users\\)[^\\]+', '$1<user>'
    $m = $m -replace '(?<![\\:A-Za-z0-9.$-])[A-Za-z0-9][A-Za-z0-9-]{1,14}\\[A-Za-z0-9._$-]{2,30}(?![\\A-Za-z0-9._$-])', '<domain>\<user>'
    return (Cut $m 200)
}
function Get-Prop($props, [int]$i) {
    if ($props.Count -gt $i -and $null -ne $props[$i].Value) { return [string]$props[$i].Value }
    return $null
}
function Read-Events($filter) {
    try { return , @(Get-WinEvent -FilterHashtable $filter -MaxEvents 500 -ErrorAction Stop) }
    catch {
        if ($_.FullyQualifiedErrorId -notlike '*NoMatchingEventsFound*') { Add-Err ('eventlog: ' + $_.Exception.Message) }
        return , @()
    }
}
$start = (Get-Date).AddDays(-7)
$events = New-Object System.Collections.Generic.List[object]
$crash = 0; $hang = 0; $slow = 0; $dno = 0

$ev = Read-Events @{ LogName = 'Application'; Id = 1000, 1002; StartTime = $start }
foreach ($e in $ev) {
    if (-not ($e.ProviderName -eq 'Application Error' -or $e.ProviderName -eq 'Application Hang')) { continue }
    $props = @($e.Properties)
    $app = Get-Prop $props 0
    if ($null -eq $app -or $app -ne 'OUTLOOK.EXE') { continue }
    $o = [ordered]@{ id = [int]$e.Id; provider = [string]$e.ProviderName; timeUtc = (Iso $e.TimeCreated) }
    if ($e.Id -eq 1000) {
        $crash++
        $mod = Get-Prop $props 3; $code = Get-Prop $props 6; $ver = Get-Prop $props 1
        $o['messageShort'] = (Cut ('Outlook crash, module ' + $mod + ', code ' + $code) 200)
        if ($mod) { $o['faultingModule'] = (Cut $mod 80) }
        if ($code) { $o['exceptionCode'] = (Cut $code 20) }
        if ($ver) { $o['appVersion'] = (Cut $ver 30) }
    } else {
        $hang++
        $ver = Get-Prop $props 1
        $o['messageShort'] = 'Outlook stopped responding'
        if ($ver) { $o['appVersion'] = (Cut $ver 30) }
    }
    $events.Add($o)
}

if (-not (Over)) {
    $ev2 = Read-Events @{ LogName = 'Application'; Id = 45, 59, 26, 27, 1026; StartTime = $start }
    $n = 0
    foreach ($e in $ev2) {
        $n++
        if (($n % 50) -eq 0 -and (Over)) { break }
        $kind = $null
        if ($e.ProviderName -eq 'Outlook' -and ($e.Id -eq 45 -or $e.Id -eq 59)) { $kind = 'slow' }
        elseif ($e.ProviderName -match '^(\.NET Runtime|Microsoft Office.*|Office.*)$' -and $e.Id -ne 45 -and $e.Id -ne 59) { $kind = 'dno' }
        if ($null -eq $kind) { continue }
        $msg = ''
        try { $msg = [string]$e.Message } catch { $msg = '' }
        if ($kind -eq 'dno' -and $msg -notmatch '(?i)outlook') { continue }
        if ($kind -eq 'slow') { $slow++ } else { $dno++ }
        $events.Add([ordered]@{ id = [int]$e.Id; provider = [string]$e.ProviderName; timeUtc = (Iso $e.TimeCreated); messageShort = (Clean $msg) })
    }
}
$list = Limit $events.ToArray() 200
Emit ([ordered]@{ windowDays = 7; events = $list; counts = [ordered]@{ crash = $crash; hang = $hang; addinSlow = $slow; dotnetOffice = $dno } })
