$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $root 'artifacts\disk-ip-health'
$live = Join-Path $root 'publish'
$backup = Join-Path $root ('artifacts\disk-ip-backup-' + (Get-Date -Format yyyyMMddHHmmss))
if (-not (Test-Path (Join-Path $stage 'Nexora.Api.dll'))) { throw 'Staged API missing.' }
New-Item -ItemType Directory -Path $backup | Out-Null
Get-ChildItem -LiteralPath $live | Where-Object { $_.Name -notin 'data','logs' } | Copy-Item -Destination $backup -Recurse -Force
try {
    & (Join-Path $root 'stop.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Could not stop API.' }
    Get-ChildItem -LiteralPath $stage | Where-Object { $_.Name -notin 'data','logs','appsettings.json','helper' } | Copy-Item -Destination $live -Recurse -Force
    & (Join-Path $root 'start.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'New API failed health check.' }
    Write-Host "Deployed. Backup: $backup"
} catch {
    $failure = $_
    & (Join-Path $root 'stop.ps1')
    Get-ChildItem -LiteralPath $backup | Copy-Item -Destination $live -Recurse -Force
    & (Join-Path $root 'start.ps1')
    throw $failure
}
