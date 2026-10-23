# Starts Nexora (database, web app, and optionally your helper).
#   start.ps1                  start everything
#   start.ps1 -InstallHelper   also make YOUR helper start at every sign-in of this user (per-user Run key; no administrator rights)
#   start.ps1 -UninstallHelper remove that autostart
param([switch]$InstallHelper, [switch]$UninstallHelper)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'nexora.config.ps1')
$ops = Join-Path $PSScriptRoot 'scripts\nexora.ps1'
$exe = Join-Path $PSScriptRoot 'publish\Nexora.Api.exe'
$helper = Join-Path $PSScriptRoot 'publish\helper\Nexora.Helper.exe'
$serverUrl = "http://$($env:COMPUTERNAME.ToLower()):$NexoraPort"

if (-not (Test-Path $exe)) {
    Write-Host 'First run: building (this takes a minute)...' -ForegroundColor Cyan
    & $ops -Action Build
    if ($LASTEXITCODE -ne 0) { exit 1 }
}

if ($UninstallHelper) {
    if (Test-Path $helper) { & $helper --uninstall }
    exit 0
}

& $ops -Action Start -Port $NexoraPort
if ($LASTEXITCODE -ne 0) { exit 1 }

if ($InstallHelper) {
    if (-not (Test-Path $helper)) { Write-Host '[FAIL] The helper is not published yet.' -ForegroundColor Red; exit 1 }
    & $helper --install --no-run --server $serverUrl
    Write-Host '[ OK ] Helper autostart installed for your user (HKCU Run)' -ForegroundColor Green
}

if ($ManageHelper) {
    if ((Get-OwnHelperProcess).Count -eq 0 -and (Test-Path $helper)) {
        Start-Process -FilePath $helper -ArgumentList '--server', $serverUrl, '--hidden' -WindowStyle Hidden
        Write-Host '[ OK ] Helper started in your session' -ForegroundColor Green
    }
}
exit 0
