[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PgBin,
    [Parameter(Mandatory)][ValidateRange(1024,65535)][int]$Port,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z_][A-Za-z0-9_]{0,62}$')][string]$Database,
    [string]$Username='postgres', [string]$HostName='127.0.0.1',
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$output=[IO.Path]::GetFullPath($OutputDirectory)
if($output -eq [IO.Path]::GetPathRoot($output)){throw 'A dedicated backup subdirectory is required.'}
if((Test-Path -LiteralPath $output) -and @(Get-ChildItem -LiteralPath $output -Force).Count -gt 0){throw 'Use a new empty directory for each backup.'}
if(-not(Test-Path -LiteralPath $output)){New-Item -ItemType Directory -Path $output | Out-Null}
$dir=Get-Item -LiteralPath $output
if($dir.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked backup directory refused.'}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$acl=New-Object Security.AccessControl.DirectorySecurity
$acl.SetOwner($identity.User); $acl.SetAccessRuleProtection($true,$false)
foreach($sid in @($identity.User,(New-Object Security.Principal.SecurityIdentifier 'S-1-5-18'),(New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'))){
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow')))
}
Set-Acl -LiteralPath $output -AclObject $acl
$file=Join-Path $output ('nexora-'+[DateTime]::UtcNow.ToString('yyyyMMddHHmmss')+'-'+[Guid]::NewGuid().ToString('N')+'.dump')
$dump=Join-Path $PgBin 'pg_dump.exe'
# Passwords are supplied only by the caller's protected PGPASSFILE / pgpass, never command arguments or report files.
& $dump --host $HostName --port $Port --username $Username --dbname $Database --no-password --format=custom --no-owner --no-acl --file ($file+'.partial')
if($LASTEXITCODE -ne 0){throw 'pg_dump failed. Partial output is retained for authorized investigation; no successful manifest was created.'}
& (Join-Path $PgBin 'pg_restore.exe') --list ($file+'.partial') | Out-Null
if($LASTEXITCODE -ne 0){throw 'Backup archive could not be read.'}
Move-Item -LiteralPath ($file+'.partial') -Destination $file
$psql=Join-Path $PgBin 'psql.exe'
$server=& $psql --host $HostName --port $Port --username $Username --dbname $Database --no-password -X -A -t -v ON_ERROR_STOP=1 -c "SELECT current_setting('server_version_num')"
if($LASTEXITCODE -ne 0){throw 'Could not record source server version.'}
$migrationIds=@()
$hasHistory=& $psql --host $HostName --port $Port --username $Username --dbname $Database --no-password -X -A -t -v ON_ERROR_STOP=1 -c "SELECT count(*) FROM information_schema.tables WHERE table_schema='public' AND table_name='__EFMigrationsHistory'"
if($LASTEXITCODE -ne 0){throw 'Could not inspect migration metadata.'}
if(([string]$hasHistory).Trim() -eq '1'){
    $query=Join-Path $output 'migration-inventory.sql'
    Set-Content -LiteralPath $query -Value 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";' -Encoding ASCII
    try{$migrationIds=@(& $psql --host $HostName --port $Port --username $Username --dbname $Database --no-password -X -A -t -v ON_ERROR_STOP=1 --file $query);if($LASTEXITCODE -ne 0){throw 'Migration inventory query failed.'}}
    finally{Remove-Item -LiteralPath $query}
}
[pscustomobject]@{Format='nexora-pg-backup-v1';Archive=[IO.Path]::GetFileName($file);Sha256=(Get-FileHash -LiteralPath $file).Hash;
    CreatedUtc=[DateTime]::UtcNow.ToString('o');Database=$Database;ServerVersionNum=([string]$server).Trim();MigrationIds=$migrationIds;ToolVersion=(& $dump --version)} | ConvertTo-Json | Set-Content -LiteralPath ($file+'.json') -Encoding UTF8
Write-Output ($file+'.json')
