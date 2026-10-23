<# Read-only, local evidence collector dispatched through diag.collect under the operator's helper.
   Windows PowerShell 5.1; no remoting, external modules, repairs, or configuration changes.
   Every failed section is null, never healthy by default. The helper enforces the outer deadline. #>
[CmdletBinding()]
param([int]$BudgetSeconds = 20)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$watch = [Diagnostics.Stopwatch]::StartNew()
$errors = New-Object System.Collections.Generic.List[string]
$data = [ordered]@{
    schema = 1; collector = 'workstation-checks'; collectedUtc = [DateTime]::UtcNow.ToString('o')
    incomplete = $false; errors = @(); elapsedMs = 0
    spoolerState = $null; updateState = $null; timeState = $null; defenderState = $null
    automaticStopped = $null; gatewayConfigured = $null; dnsConfigured = $null
    domainJoined = $null; domainResolved = $null; policyErrors = $null; temporaryProfiles = $null
}
function Read-Section([string]$name, [scriptblock]$read) {
    if ($watch.Elapsed.TotalSeconds -ge $BudgetSeconds) { $errors.Add($name + ': time budget'); return }
    try { & $read } catch { $errors.Add($name + ': unavailable') }
}
Read-Section 'services' {
    $services = @(Get-CimInstance -ClassName Win32_Service -OperationTimeoutSec 3)
    $data.automaticStopped = @($services | Where-Object { $_.StartMode -eq 'Auto' -and $_.State -eq 'Stopped' }).Count
    foreach ($pair in @(@('Spooler','spoolerState'), @('wuauserv','updateState'), @('W32Time','timeState'), @('WinDefend','defenderState'))) {
        $service = $services | Where-Object { $_.Name -eq $pair[0] } | Select-Object -First 1
        if ($null -ne $service) { $data[$pair[1]] = [string]$service.State }
    }
}
Read-Section 'network configuration' {
    $adapters = @(Get-CimInstance -ClassName Win32_NetworkAdapterConfiguration -Filter 'IPEnabled=True' -OperationTimeoutSec 3)
    $data.gatewayConfigured = @($adapters | Where-Object { @($_.DefaultIPGateway).Count -gt 0 -and $null -ne $_.DefaultIPGateway }).Count -gt 0
    $data.dnsConfigured = @($adapters | Where-Object { @($_.DNSServerSearchOrder).Count -gt 0 -and $null -ne $_.DNSServerSearchOrder }).Count -gt 0
}
Read-Section 'domain' {
    $computer = Get-CimInstance -ClassName Win32_ComputerSystem -OperationTimeoutSec 3
    $data.domainJoined = [bool]$computer.PartOfDomain
    if ($data.domainJoined -and -not [string]::IsNullOrWhiteSpace([string]$computer.Domain)) {
        $lookup = [Net.Dns]::BeginGetHostAddresses([string]$computer.Domain, $null, $null)
        try {
            if ($lookup.AsyncWaitHandle.WaitOne(1500)) {
                try { $data.domainResolved = @([Net.Dns]::EndGetHostAddresses($lookup)).Count -gt 0 }
                catch { $data.domainResolved = $false }
            } else { $errors.Add('domain DNS: timed out') }
        } finally { $lookup.AsyncWaitHandle.Close() }
    }
}
Read-Section 'group policy events' {
    # No matching events is zero; an inaccessible/missing log remains null.
    $events = @(Get-WinEvent -FilterHashtable @{ LogName='Microsoft-Windows-GroupPolicy/Operational'; Level=2; StartTime=(Get-Date).AddHours(-24) } -MaxEvents 20 -ErrorAction SilentlyContinue -ErrorVariable eventErrors)
    $unexpected = @($eventErrors | Where-Object { $_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*' })
    if ($unexpected.Count -gt 0) { throw 'Policy log unavailable' }
    $data.policyErrors = $events.Count
}
Read-Section 'profiles' {
    $profiles = @(Get-CimInstance -ClassName Win32_UserProfile -OperationTimeoutSec 3)
    $data.temporaryProfiles = @($profiles | Where-Object { -not $_.Special -and $_.SID -match '^S-1-5-21-' -and (([int]$_.Status -band 1) -ne 0) }).Count
}
$data.errors = @($errors)
$data.elapsedMs = [int]$watch.ElapsedMilliseconds
# Partial sections remain null. Rules that need those fields are explicitly reported as not evaluated.
ConvertTo-Json -InputObject $data -Depth 4 -Compress
