<#
  Nexora F5 collector: office-version
  Installed Office/Outlook version, build and update channel.
  Read-only, Windows PowerShell 5.1, no modules. Emits exactly one JSON object on stdout.
  Schema: docs/diagnostics/collector-schemas.md
#>
[CmdletBinding()]
param([int]$BudgetSeconds = 25)
$CollectorId = 'office-version'
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

$channels = @{
    '492350f6-3a01-4f97-b9c0-c7c6ddf67d60' = 'Current Channel'
    '64256afe-f5d9-4f86-8936-8840a6a4f5be' = 'Current Channel (Preview)'
    '55336b82-a18d-4dd6-b5f6-9e5095c314a6' = 'Monthly Enterprise Channel'
    '7ffbc6bf-bc32-4f92-8982-f9dd17fd3114' = 'Semi-Annual Enterprise Channel'
    'b8f9b850-328d-4355-9145-c59439a0c4cf' = 'Semi-Annual Enterprise Channel (Preview)'
    '5440fd1f-7ecb-4221-8110-145efaa6372f' = 'Beta Channel'
    'f2e724c1-748f-4b47-8fb8-8e0d210e9208' = 'Office LTSC / perpetual'
    '5030841d-c919-4594-8d2d-84ae4f96e58e' = 'Office LTSC / perpetual'
    '1d2d2ea6-1680-4c56-ac58-a441c8c24ff9' = 'Office LTSC / perpetual'
}
$installed = $null
try {
    $c2r = 'HKLM:\SOFTWARE\Microsoft\Office\ClickToRun\Configuration'
    if (Test-Path -LiteralPath $c2r) {
        $ver = [string](Get-Val $c2r 'VersionToReport')
        $cdn = [string](Get-Val $c2r 'CDNBaseUrl')
        $upd = [string](Get-Val $c2r 'UpdateChannel')
        $raw = $cdn
        if ([string]::IsNullOrEmpty($raw)) { $raw = $upd }
        $chan = 'Unknown'
        foreach ($k in $channels.Keys) { if ($raw -like ('*' + $k + '*')) { $chan = $channels[$k]; break } }
        $plat = [string](Get-Val $c2r 'Platform')
        $bit = $null
        if ($plat -eq 'x64') { $bit = 'x64' } elseif ($plat -eq 'x86') { $bit = 'x86' }
        $ue = Get-Val $c2r 'UpdatesEnabled'
        $uen = $null
        if ($null -ne $ue) { $uen = ([string]$ue -match '^(1|true)$') }
        $build = $ver
        if ($ver -match '^\d+\.\d+\.(\d+\.\d+)$') { $build = $Matches[1] }
        $o = [ordered]@{
            productName = (Cut ([string](Get-Val $c2r 'ProductReleaseIds')) 120); version = $ver; build = $build; channel = $chan
            channelRaw = (Cut $raw 120); buildReleaseDateUtc = $null; bitness = $bit; updatesEnabled = $uen
        }
        $lc = [string](Get-Val 'HKLM:\SOFTWARE\Microsoft\Office\ClickToRun\Updates' 'UpdateDetectionLastRunTime')
        $dt = [datetime]::MinValue
        if ($lc -and [datetime]::TryParse($lc, [cultureinfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeLocal, [ref]$dt)) { $o['lastUpdateCheckUtc'] = (Iso $dt) }
        $o['installType'] = 'ClickToRun'
        $installed = $o
    } else {
        $exe = [string](Get-Val 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\OUTLOOK.EXE' '(default)')
        if ($exe -and (Test-Path -LiteralPath $exe)) {
            $vi = (Get-Item -LiteralPath $exe).VersionInfo
            $ver = ('{0}.{1}.{2}.{3}' -f $vi.FileMajorPart, $vi.FileMinorPart, $vi.FileBuildPart, $vi.FilePrivatePart)
            $bit = 'x64'
            if ($exe -like '*Program Files (x86)*') { $bit = 'x86' }
            $installed = [ordered]@{ productName = (Cut ([string]$vi.ProductName) 120); version = $ver; build = ('{0}.{1}' -f $vi.FileBuildPart, $vi.FilePrivatePart); channel = 'n/a'; channelRaw = $null; buildReleaseDateUtc = $null; bitness = $bit; updatesEnabled = $null; installType = 'Msi' }
        }
    }
} catch { Add-Err ('office: ' + $_.Exception.Message) }
Emit ([ordered]@{ installed = $installed })
