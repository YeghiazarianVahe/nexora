[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$output=[IO.Path]::GetFullPath($OutputDirectory)
foreach($protected in 'publish','run','.git'){
    $path=[IO.Path]::GetFullPath((Join-Path $root $protected))
    if($output -eq $path -or $output.StartsWith($path+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Candidate output must not be a production/runtime/Git path.'}
}
if(Test-Path -LiteralPath $output){if(@(Get-ChildItem -LiteralPath $output -Force).Count -gt 0){throw 'Candidate output must be empty.'}}
else{New-Item -ItemType Directory -Path $output|Out-Null}
if((Get-Item -LiteralPath $output).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked candidate output refused.'}
$build=Join-Path $output 'build'
Push-Location $root
try{
    dotnet --info | Set-Content (Join-Path $output 'toolchain.txt')
    node --version | Add-Content (Join-Path $output 'toolchain.txt')
    $commit=(git rev-parse HEAD).Trim()
    git status --porcelain=v1 | Set-Content (Join-Path $output 'working-tree.txt')
    $version='1.0.0-rc.'+$commit.Substring(0,12)+'.working-tree'
    $web=Join-Path $output 'web'
    Push-Location src/Nexora.Web
    try{
        npm run typecheck; if($LASTEXITCODE -ne 0){throw 'Typecheck failed.'}
        node node_modules/vite/bin/vite.js build --outDir $web
        if($LASTEXITCODE -ne 0){throw 'Web build failed.'}
        npm ls --all --json | Set-Content (Join-Path $output 'npm-inventory.json')
        if($LASTEXITCODE -ne 0){throw 'Installed dependency graph is invalid; resolve peer/dependency problems before packaging.'}
    }finally{Pop-Location}
    dotnet publish src/Nexora.Api/Nexora.Api.csproj -c Release --artifacts-path $build -o (Join-Path $output 'api') -p:NuGetAudit=false "-p:InformationalVersion=$version" --disable-build-servers
    if($LASTEXITCODE -ne 0){throw 'API publish failed.'}
    dotnet publish src/Nexora.Helper/Nexora.Helper.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true --artifacts-path $build -o (Join-Path $output 'helper') -p:NuGetAudit=false "-p:InformationalVersion=$version" --disable-build-servers
    if($LASTEXITCODE -ne 0){throw 'Helper publish failed.'}
    $wwwroot=Join-Path $output 'api\wwwroot'
    $resolved=[IO.Path]::GetFullPath($wwwroot)
    if(-not $resolved.StartsWith($output+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid candidate web path.'}
    if(Test-Path -LiteralPath $wwwroot){Remove-Item -LiteralPath $wwwroot -Recurse -Force}
    New-Item -ItemType Directory -Path $wwwroot | Out-Null
    Get-ChildItem -LiteralPath $web | Copy-Item -Destination $wwwroot -Recurse -Force
    $scripts=Join-Path $output 'scripts';New-Item -ItemType Directory -Path $scripts|Out-Null
    Get-ChildItem -LiteralPath $PSScriptRoot -Filter *.ps1 | Copy-Item -Destination $scripts
    Copy-Item -LiteralPath src/Nexora.Infrastructure/Wmi/native-nvme-read.ps1 -Destination (Join-Path $scripts 'native-disk-telemetry.ps1')
    dotnet list Nexora.sln package --include-transitive --no-restore --format json | Set-Content (Join-Path $output 'dotnet-inventory.json')
    node tools/New-Sbom.mjs $output
    if($LASTEXITCODE -ne 0){throw 'SBOM inventory generation failed.'}
    $migrations=@(Get-ChildItem src/Nexora.Infrastructure/Persistence/Migrations -Filter *.Designer.cs | Select-String '\[Migration\("([^"]+)"\)\]' | ForEach-Object{$_.Matches[0].Groups[1].Value}|Sort-Object)
    ConvertTo-Json -InputObject $migrations|Set-Content (Join-Path $output 'migrations.json')
    [pscustomobject]@{Version=$version;BaseCommit=$commit;Immutable=$false;Signed=$false;CreatedUtc=[DateTime]::UtcNow.ToString('o');DeploymentAuthorized=$false}|ConvertTo-Json|Set-Content (Join-Path $output 'candidate.json')
    $sources=foreach($file in (git ls-files --cached --others --exclude-standard -- src scripts tests tools docs Directory.Build.props Nexora.sln)){
        if(Test-Path -LiteralPath $file -PathType Leaf){[pscustomobject]@{Path=$file;Sha256=(Get-FileHash -LiteralPath $file).Hash}}
    }
    $sources|ConvertTo-Json|Set-Content (Join-Path $output 'source-manifest.json')
    $manifest=foreach($dir in 'api','web','helper','scripts'){Get-ChildItem -LiteralPath (Join-Path $output $dir) -Recurse -File|ForEach-Object{[pscustomobject]@{Path=$_.FullName.Substring($output.Length+1);Bytes=$_.Length;Sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}}}
    $manifest|ConvertTo-Json|Set-Content (Join-Path $output 'sha256-manifest.json')
}finally{Pop-Location}
Write-Output "Unsigned, unstamped working-tree candidate prepared at $output. No deployment performed."
