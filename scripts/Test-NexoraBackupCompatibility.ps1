[CmdletBinding()]
param([Parameter(Mandatory)][string]$Manifest,[Parameter(Mandatory)][string]$CandidateMigrations)
$ErrorActionPreference='Stop'
$backup=Get-Content -LiteralPath $Manifest -Raw|ConvertFrom-Json
$candidate=ConvertFrom-Json -InputObject (Get-Content -LiteralPath $CandidateMigrations -Raw)
if($backup.Format -ne 'nexora-pg-backup-v1'){throw 'Invalid backup manifest.'}
$unknown=@($backup.MigrationIds|Where-Object{$_ -notin $candidate})
if($unknown.Count){throw 'Backup includes migrations unknown to the candidate. Binary rollback is not safe.'}
$pending=@($candidate|Where-Object{$_ -notin $backup.MigrationIds})
[pscustomobject]@{UnknownMigrationCount=0;PendingMigrationCount=$pending.Count;RequiresOfflineMigrationRehearsal=($pending.Count -gt 0);RestoreVerified=$false}
