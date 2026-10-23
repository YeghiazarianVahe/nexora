[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PgBin,
    [Parameter(Mandatory)][ValidateRange(1024,65535)][int]$Port,
    [Parameter(Mandatory)][ValidatePattern('^nexora_restore_[a-z0-9_]{1,40}$')][string]$Database,
    [string]$Username='postgres',
    [Parameter(Mandatory)][string]$Manifest,
    [Parameter(Mandatory)][string]$DisposableMarker
)
$ErrorActionPreference='Stop'
if($Port -eq 5434){throw 'The production private database port is forbidden for disposable restore.'}
if((Get-Content -LiteralPath $DisposableMarker -Raw).Trim() -notmatch '^nexora-disposable-restore-v1:[a-f0-9]{32}$'){throw 'A valid disposable-target marker is required.'}
$meta=Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json
if($meta.Format -ne 'nexora-pg-backup-v1' -or [IO.Path]::GetFileName($meta.Archive) -ne $meta.Archive){throw 'Invalid backup manifest.'}
$archive=Join-Path (Split-Path -Parent ([IO.Path]::GetFullPath($Manifest))) $meta.Archive
if((Get-FileHash -LiteralPath $archive).Hash -ne $meta.Sha256){throw 'Backup checksum mismatch.'}
$tables=& (Join-Path $PgBin 'psql.exe') --host 127.0.0.1 --port $Port --username $Username --dbname $Database --no-password -X -A -t -v ON_ERROR_STOP=1 -c "SELECT count(*) FROM information_schema.tables WHERE table_schema='public'"
if($LASTEXITCODE -ne 0 -or ([string]$tables).Trim() -ne '0'){throw 'Disposable target must exist and have no public tables.'}
& (Join-Path $PgBin 'pg_restore.exe') --host 127.0.0.1 --port $Port --username $Username --dbname $Database --no-password --exit-on-error --single-transaction --no-owner --no-acl $archive
if($LASTEXITCODE -ne 0){throw 'Disposable restore failed; transaction rolled back.'}
Write-Output 'Archive restored to the empty disposable target. Verify application rows, constraints, migrations and audit triggers before claiming recovery.'
