# Stops Nexora (web app and the helper). The private database is left running. No arguments.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'nexora.config.ps1')

if ($ManageHelper) {
    $h = Get-OwnHelperProcess
    if ($h.Count -gt 0) { $h | Stop-Process -Force; Write-Host '[ OK ] Your helper stopped' -ForegroundColor Green }
}

& (Join-Path $PSScriptRoot 'scripts\nexora.ps1') -Action Stop -Port $NexoraPort
exit $LASTEXITCODE
