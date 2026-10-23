<#
  Nexora F5 collector: system-resources
  Disk space, memory, CPU load and Windows Search state.
  Read-only, Windows PowerShell 5.1, no modules. Emits exactly one JSON object on stdout.
  Schema: docs/diagnostics/collector-schemas.md
#>
[CmdletBinding()]
param([int]$BudgetSeconds = 25)
$CollectorId = 'system-resources'
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

function Pct([double]$part, [double]$whole) { if ($whole -le 0) { return $null }; return [math]::Round(100.0 * $part / $whole, 1) }
$vols = @(); $sysFree = $null
try {
    $sysLetter = ([string]$env:SystemDrive).TrimEnd(':').ToUpperInvariant()
    foreach ($d in @(Get-CimInstance -ClassName Win32_LogicalDisk -Filter 'DriveType=3')) {
        if ($null -eq $d.Size -or [double]$d.Size -le 0) { continue }
        $letter = ([string]$d.DeviceID).TrimEnd(':').ToUpperInvariant()
        $o = [ordered]@{ letter = $letter }
        if ($d.VolumeName) { $o['label'] = (Cut ([string]$d.VolumeName) 32) }
        $o['sizeBytes'] = [int64]$d.Size; $o['freeBytes'] = [int64]$d.FreeSpace; $o['freePercent'] = (Pct $d.FreeSpace $d.Size)
        if ($letter -eq $sysLetter) { $sysFree = $o['freePercent'] }
        $vols += , $o
    }
} catch { Add-Err ('disk: ' + $_.Exception.Message) }
$vols = Limit $vols 26

$mem = [ordered]@{ totalBytes = 0; freeBytes = 0; usedPercent = 0; pageFileUsedPercent = $null }
try {
    $os = Get-CimInstance -ClassName Win32_OperatingSystem
    $t = [int64]$os.TotalVisibleMemorySize * 1024; $f = [int64]$os.FreePhysicalMemory * 1024
    $mem.totalBytes = $t; $mem.freeBytes = $f; $mem.usedPercent = (Pct ($t - $f) $t)
} catch { Add-Err ('memory: ' + $_.Exception.Message) }
try {
    $alloc = 0.0; $cur = 0.0
    foreach ($p in @(Get-CimInstance -ClassName Win32_PageFileUsage)) { $alloc += [double]$p.AllocatedBaseSize; $cur += [double]$p.CurrentUsage }
    if ($alloc -gt 0) { $mem.pageFileUsedPercent = (Pct $cur $alloc) }
} catch { Add-Err ('pagefile: ' + $_.Exception.Message) }

$cpu = [ordered]@{ loadPercent = $null; logicalProcessors = 0 }
try {
    $processors = @(Get-CimInstance -ClassName Win32_Processor)
    $cpu.logicalProcessors = [int](($processors | Measure-Object -Property NumberOfLogicalProcessors -Sum).Sum)
    $samples = @()
    for ($i = 0; $i -lt 3; $i++) {
        if ($i -gt 0) { if (Over) { break }; Start-Sleep -Milliseconds 500 }
        $load = $null
        try {
            $total = Get-CimInstance -ClassName Win32_PerfFormattedData_Counters_ProcessorInformation -Filter "Name='_Total'"
            if ($null -ne $total -and $null -ne $total.PercentProcessorUtility -and [double]$total.PercentProcessorUtility -ge 0 -and [double]$total.PercentProcessorUtility -le 1000) { $load = [math]::Min(100, [double]$total.PercentProcessorUtility) }
        } catch { Write-Verbose 'CPU utility unavailable; trying processor time.' }
        if ($null -eq $load) {
            try {
                $total = Get-CimInstance -ClassName Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'"
                if ($null -ne $total -and $null -ne $total.PercentProcessorTime -and [double]$total.PercentProcessorTime -ge 0 -and [double]$total.PercentProcessorTime -le 100) { $load = [double]$total.PercentProcessorTime }
            } catch { Write-Verbose 'CPU processor time unavailable; trying socket load.' }
        }
        if ($null -eq $load) {
            $weighted = 0.0; $cores = 0
            foreach ($p in @(Get-CimInstance -ClassName Win32_Processor)) {
                if ($null -ne $p.LoadPercentage -and [double]$p.LoadPercentage -ge 0 -and [double]$p.LoadPercentage -le 100) {
                    $weight = [math]::Max(1, [int]$p.NumberOfLogicalProcessors)
                    $weighted += [double]$p.LoadPercentage * $weight; $cores += $weight
                }
            }
            if ($cores -gt 0) { $load = $weighted / $cores }
        }
        if ($null -ne $load) { $samples += $load }
    }
    if ($samples.Count -gt 0) { $cpu.loadPercent = [math]::Round((($samples | Measure-Object -Average).Average), 1) }
} catch { Add-Err ('cpu: ' + $_.Exception.Message) }

$search = [ordered]@{ serviceState = $null; startMode = $null; indexing = [ordered]@{ state = $null; itemsToIndex = $null; catalogSizeMB = $null } }
try {
    $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='WSearch'"
    if ($null -ne $svc) { $search.serviceState = [string]$svc.State; $search.startMode = [string]$svc.StartMode }
} catch { Add-Err ('search: ' + $_.Exception.Message) }
try {
    $edb = Join-Path $env:ProgramData 'Microsoft\Search\Data\Applications\Windows\Windows.edb'
    if (Test-Path -LiteralPath $edb) { $search.indexing.catalogSizeMB = [math]::Round(((New-Object System.IO.FileInfo $edb).Length / 1MB), 1) }
} catch { Write-Verbose 'catalog size unavailable' }

Emit ([ordered]@{ disk = [ordered]@{ systemDriveFreePercent = $sysFree; volumes = $vols }; memory = $mem; cpu = $cpu; search = $search })
