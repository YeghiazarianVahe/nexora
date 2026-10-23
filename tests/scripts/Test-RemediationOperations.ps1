$ErrorActionPreference='Stop'
$root=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$directory=Join-Path $env:TEMP ('nexora-operationtest-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory|Out-Null
try{
    $configPath=Join-Path $directory 'input.json';$safe=Join-Path $directory 'safe.json'
    Set-Content -LiteralPath $configPath -Value '{"Nexora":{"Url":"http://test","SuperAdmin":{"Sid":"S-1-test","Account":"TEST","Password":"SYNTHETIC_SECRET"}},"ConnectionStrings":{"Nexora":"SYNTHETIC_SECRET"},"Features":{"Diagnostics":true,"CredentialOps":false},"DiskHealth":{"HddMethod":"smart-raw"}}'
    & (Join-Path $root 'scripts\Backup-NexoraConfiguration.ps1') -InputConfig $configPath -OutputFile $safe
    if((Get-Content -LiteralPath $safe -Raw).Contains('SYNTHETIC_SECRET')){throw 'Nonsecret configuration export leaked synthetic sensitive data'}
    $manifest=Join-Path $directory 'manifest.json';$candidate=Join-Path $directory 'migrations.json'
    Set-Content -LiteralPath $manifest -Value '{"Format":"nexora-pg-backup-v1","MigrationIds":["old"]}'
    Set-Content -LiteralPath $candidate -Value '["old","new"]'
    $result=& (Join-Path $root 'scripts\Test-NexoraBackupCompatibility.ps1') -Manifest $manifest -CandidateMigrations $candidate
    if($result.PendingMigrationCount -ne 1 -or -not $result.RequiresOfflineMigrationRehearsal){throw 'Pending migration was not reported'}
    Set-Content -LiteralPath $manifest -Value '{"Format":"nexora-pg-backup-v1","MigrationIds":["unknown"]}'
    $rejected=$false
    try{& (Join-Path $root 'scripts\Test-NexoraBackupCompatibility.ps1') -Manifest $manifest -CandidateMigrations $candidate}catch{$rejected=$true}
    if(-not $rejected){throw 'Unknown future migration was accepted'}
    Write-Output 'PASS: synthetic secret omitted; known upgrade needs rehearsal; unknown migration prevents rollback compatibility.'
}finally{
    $resolved=[IO.Path]::GetFullPath($directory)
    if(-not $resolved.StartsWith([IO.Path]::GetFullPath($env:TEMP)+[IO.Path]::DirectorySeparatorChar+'nexora-operationtest-',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe temporary cleanup path'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
