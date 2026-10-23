[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $root 'artifacts\server-reliability'
$live = Join-Path $root 'publish'
$backup = Join-Path $root ('artifacts\production-backup-' + (Get-Date -Format yyyyMMddHHmmss))
$ops = Join-Path $PSScriptRoot 'nexora.ps1'
if (-not (Test-Path (Join-Path $stage 'helper\Nexora.Helper.exe'))) { throw 'Staged release is incomplete.' }
New-Item -ItemType Directory -Path $backup | Out-Null
Get-ChildItem -LiteralPath $live | Where-Object { $_.Name -notin 'data','logs' } | Copy-Item -Destination $backup -Recurse -Force
$config = Get-Content (Join-Path $live 'appsettings.json') -Raw | ConvertFrom-Json
$config.Features.Diagnostics = $true
try {
    & (Join-Path $root 'stop.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Could not stop Nexora.' }
    & $ops -Action DbStop
    if ($LASTEXITCODE -ne 0) { throw 'Could not stop the old database process.' }
    Get-ChildItem -LiteralPath $stage | Where-Object { $_.Name -notin 'data','logs','appsettings.json' } | Copy-Item -Destination $live -Recurse -Force
    # Keep the production identity, operator list and other settings.
    $config | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $live 'appsettings.json') -Encoding UTF8
    & (Join-Path $root 'start.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'The new release did not become healthy.' }
    Write-Host "Production release started. Rollback files: $backup"
} catch {
    $failure = $_
    & (Join-Path $root 'stop.ps1')
    Get-ChildItem -LiteralPath $backup | Copy-Item -Destination $live -Recurse -Force
    & (Join-Path $root 'start.ps1')
    throw $failure
}
