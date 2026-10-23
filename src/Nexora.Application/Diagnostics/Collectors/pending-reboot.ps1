<#
  Nexora F5 collector: pending-reboot
  Pending-reboot signals (CBS, WU, PFRO, rename, ConfigMgr).
  Read-only, Windows PowerShell 5.1, no modules. Emits exactly one JSON object on stdout.
  Schema: docs/diagnostics/collector-schemas.md
#>
[CmdletBinding()]
param([int]$BudgetSeconds = 25)
$CollectorId = 'pending-reboot'
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

$sig = [ordered]@{ cbsRebootPending = $false; windowsUpdateRebootRequired = $false; pendingFileRenameOperations = $false; computerRenamePending = $false; ccmRebootPending = $null }
try { $sig.cbsRebootPending = [bool](Test-Path -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') } catch { Add-Err ('cbs: ' + $_.Exception.Message) }
try { $sig.windowsUpdateRebootRequired = [bool](Test-Path -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired') } catch { Add-Err ('wu: ' + $_.Exception.Message) }
try {
    $v = Get-Val 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' 'PendingFileRenameOperations'
    $sig.pendingFileRenameOperations = [bool](@($v | Where-Object { -not [string]::IsNullOrEmpty([string]$_) }).Count -gt 0)
} catch { Add-Err ('pfro: ' + $_.Exception.Message) }
try {
    $a = Get-Val 'HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\ActiveComputerName' 'ComputerName'
    $n = Get-Val 'HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName' 'ComputerName'
    if ($null -ne $a -and $null -ne $n) { $sig.computerRenamePending = ([string]$a -ne [string]$n) }
} catch { Add-Err ('rename: ' + $_.Exception.Message) }
if (-not (Over)) {
    try {
        $r = Invoke-CimMethod -Namespace 'root\ccm\ClientSDK' -ClassName 'CCM_ClientUtilities' -MethodName 'DetermineIfRebootPending' -ErrorAction Stop
        $sig.ccmRebootPending = [bool]($r.RebootPending -or $r.IsHardRebootPending)
    } catch { $sig.ccmRebootPending = $null }
}
$pending = [bool]($sig.cbsRebootPending -or $sig.windowsUpdateRebootRequired -or $sig.pendingFileRenameOperations -or $sig.computerRenamePending -or ($sig.ccmRebootPending -eq $true))
Emit ([ordered]@{ signals = $sig; pending = $pending })
