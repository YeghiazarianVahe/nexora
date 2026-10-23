<#
  Nexora F5 collector: network-probe
  DNS + ICMP + TCP 443 connect probe of up to 5 hosts. No credentials, no HTTP.
  Read-only, Windows PowerShell 5.1, no modules. Emits exactly one JSON object on stdout.
  Schema: docs/diagnostics/collector-schemas.md
#>
[CmdletBinding()]
param([int]$BudgetSeconds = 25, [string[]]$Targets = @())
$CollectorId = 'network-probe'
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

$valid = New-Object System.Collections.Generic.List[string]
$all = @()
foreach ($t in @($Targets)) {
    foreach ($piece in ([string]$t -split ',')) { $x = $piece.Trim(); if ($x.Length -gt 0) { $all += $x } }
}
foreach ($h in $all) {
    if ($h -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$') { Add-Err 'invalid target skipped'; continue }
    if ($valid.Count -ge 5) { Add-Err ('target skipped (max 5): ' + (Cut $h 60)); continue }
    $valid.Add($h)
}
if ($valid.Count -eq 0) { $script:incomplete = $true; $script:reason = 'no targets' }
$rows = @()
foreach ($h in $valid) {
    if (Over) { break }
    $row = [ordered]@{ host = $h; resolved = $false; dnsMs = $null; addresses = @(); pingSent = 0; pingReceived = 0; lossPercent = $null; minMs = $null; avgMs = $null; maxMs = $null; tcp443 = $null }
    try {
        $d = [Diagnostics.Stopwatch]::StartNew()
        $ar = [System.Net.Dns]::BeginGetHostAddresses($h, $null, $null)
        $addrs = @()
        if ($ar.AsyncWaitHandle.WaitOne(3000)) {
            try { $addrs = @([System.Net.Dns]::EndGetHostAddresses($ar)) } catch { $addrs = @() }
        }
        $row['dnsMs'] = [int]$d.ElapsedMilliseconds
        if ($addrs.Count -gt 0) {
            $row['resolved'] = $true
            $row['addresses'] = @($addrs | Select-Object -First 4 | ForEach-Object { $_.IPAddressToString })
            $target = $addrs[0]
            foreach ($a in $addrs) { if ($a.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork) { $target = $a; break } }

            $sent = 0; $times = @()
            $ping = New-Object System.Net.NetworkInformation.Ping
            try {
                for ($i = 0; $i -lt 4; $i++) {
                    if (Over) { break }
                    $sent++
                    try {
                        $r = $ping.Send($target, 1000)
                        if ($r.Status -eq [System.Net.NetworkInformation.IPStatus]::Success) { $times += [int]$r.RoundtripTime }
                    } catch { Write-Verbose 'ping failed' }
                    if ($i -lt 3) { Start-Sleep -Milliseconds 100 }
                }
            } finally { $ping.Dispose() }
            $row['pingSent'] = $sent; $row['pingReceived'] = $times.Count
            if ($sent -gt 0) { $row['lossPercent'] = [math]::Round(100.0 * ($sent - $times.Count) / $sent, 1) }
            if ($times.Count -gt 0) {
                $m = $times | Measure-Object -Minimum -Maximum -Average
                $row['minMs'] = [int]$m.Minimum; $row['maxMs'] = [int]$m.Maximum; $row['avgMs'] = [math]::Round($m.Average, 1)
            }

            if (-not (Over)) {
                $tc = New-Object System.Net.Sockets.TcpClient
                try {
                    $t0 = [Diagnostics.Stopwatch]::StartNew()
                    $reach = $false
                    $iar = $tc.BeginConnect($target, 443, $null, $null)
                    if ($iar.AsyncWaitHandle.WaitOne(1500)) {
                        try { $tc.EndConnect($iar); $reach = $true } catch { $reach = $false }
                    }
                    $ms = $null
                    if ($reach) { $ms = [int]$t0.ElapsedMilliseconds }
                    $row['tcp443'] = [ordered]@{ reachable = $reach; ms = $ms }
                } finally { $tc.Close() }
            }
        }
    } catch { Add-Err ('probe: ' + $_.Exception.Message) }
    $rows += , $row
}
Emit ([ordered]@{ targets = $rows })
