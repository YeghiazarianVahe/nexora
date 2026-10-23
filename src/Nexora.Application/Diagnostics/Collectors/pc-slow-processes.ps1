<#
  Nexora F5 collector: pc-slow-processes
  Top processes by CPU (2 s sample) and by working set.
  Read-only, Windows PowerShell 5.1, no modules. Emits exactly one JSON object on stdout.
  Schema: docs/diagnostics/collector-schemas.md
#>
[CmdletBinding()]
param([int]$BudgetSeconds = 25)
$CollectorId = 'pc-slow-processes'
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

function Get-Snap {
    $h = @{}
    foreach ($p in @(Get-CimInstance -ClassName Win32_Process -Property ProcessId, Name, KernelModeTime, UserModeTime, WorkingSetSize)) {
        if ([int]$p.ProcessId -eq 0) { continue }
        $h[[int]$p.ProcessId] = $p
    }
    return $h
}
$lp = 1
try { $lp = 0; foreach ($p in @(Get-CimInstance -ClassName Win32_Processor)) { $lp += [int]$p.NumberOfLogicalProcessors }; if ($lp -lt 1) { $lp = 1 } } catch { $lp = 1 }
$rows = @(); $secs = 0.0
try {
    $t0 = [Diagnostics.Stopwatch]::StartNew()
    $a = Get-Snap
    if (-not (Over)) { Start-Sleep -Seconds 2 }
    $b = Get-Snap
    $secs = $t0.Elapsed.TotalSeconds
    foreach ($id in $b.Keys) {
        $p = $b[$id]
        $cpuPct = 0.0
        if ($a.ContainsKey($id) -and $secs -gt 0) {
            $d = ([double]$p.KernelModeTime + [double]$p.UserModeTime) - ([double]$a[$id].KernelModeTime + [double]$a[$id].UserModeTime)
            if ($d -lt 0) { $d = 0 }
            $cpuPct = [math]::Round(($d / 1.0E7) / ($secs * $lp) * 100.0, 1)
            if ($cpuPct -gt 100) { $cpuPct = 100.0 }
        }
        $rows += [pscustomobject]@{ name = (Cut ([string]$p.Name) 64); pid = [int]$id; cpuPercent = $cpuPct; workingSetBytes = [int64]$p.WorkingSetSize }
    }
} catch { Add-Err ('sample: ' + $_.Exception.Message) }
$topCpu = @($rows | Sort-Object -Property cpuPercent, workingSetBytes -Descending | Select-Object -First 15)
$topMem = @($rows | Sort-Object -Property workingSetBytes -Descending | Select-Object -First 15)
Emit ([ordered]@{ sampleSeconds = [math]::Round($secs, 1); logicalProcessors = $lp; topCpu = $topCpu; topMemory = $topMem })
