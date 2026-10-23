[CmdletBinding()]
param([Parameter(Mandatory)][string]$InputConfig,[Parameter(Mandatory)][string]$OutputFile)
$ErrorActionPreference='Stop'
$config=Get-Content -LiteralPath $InputConfig -Raw | ConvertFrom-Json
$safe=[ordered]@{Format='nexora-nonsecret-config-v1';CreatedUtc=[DateTime]::UtcNow.ToString('o');
    Url=$config.Nexora.Url;OperatorPcs=$config.Nexora.OperatorPcs;SuperAdmin=@{Sid=$config.Nexora.SuperAdmin.Sid;Account=$config.Nexora.SuperAdmin.Account};
    Features=@{Diagnostics=[bool]$config.Features.Diagnostics;CredentialOps=[bool]$config.Features.CredentialOps};
    HddMethod=$config.DiskHealth.HddMethod;SecretsOmitted=@('ConnectionStrings','private signing keys','DPAPI material','passwords');RecoveryVerified=$false}
$bytes=(New-Object Text.UTF8Encoding($false)).GetBytes(($safe|ConvertTo-Json -Depth 6))
$stream=New-Object IO.FileStream([IO.Path]::GetFullPath($OutputFile),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try{$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}
