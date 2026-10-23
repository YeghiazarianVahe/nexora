<#
  Nexora F5 collector: outlook-process
  Running OUTLOOK.EXE instances: memory, handles, threads, responsiveness.
  Read-only, Windows PowerShell 5.1, no modules. Emits exactly one JSON object on stdout.
  Schema: docs/diagnostics/collector-schemas.md
#>
[CmdletBinding()]
param([int]$BudgetSeconds = 25)
$CollectorId = 'outlook-process'
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

$inst = @()
try {
    foreach ($p in @(Get-Process -Name 'OUTLOOK' -ErrorAction SilentlyContinue)) {
        if (Over) { break }
        $resp = $null; $start = $null; $cpuS = $null; $thr = 0; $hnd = 0
        try { $resp = [bool]$p.Responding } catch { $resp = $null }
        try { $start = Iso $p.StartTime } catch { $start = $null }
        try { $cpuS = [math]::Round($p.TotalProcessorTime.TotalSeconds, 1) } catch { $cpuS = $null }
        try { $thr = [int]$p.Threads.Count } catch { $thr = 0 }
        try { $hnd = [int]$p.HandleCount } catch { $hnd = 0 }
        $inst += [ordered]@{
            pid = [int]$p.Id; workingSetBytes = [int64]$p.WorkingSet64; privateBytes = [int64]$p.PrivateMemorySize64
            handleCount = $hnd; threadCount = $thr; cpuSeconds = $cpuS; responding = $resp; startTimeUtc = $start; sessionId = [int]$p.SessionId
        }
    }
} catch { Add-Err ('process: ' + $_.Exception.Message) }
$inst = Limit $inst 50
Emit ([ordered]@{ count = @($inst).Count; instances = $inst })
