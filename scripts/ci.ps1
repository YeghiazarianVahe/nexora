<#
.SYNOPSIS
  Everything that must be green before a change is accepted: backend build (warnings are errors), TypeScript, ESLint, backend tests, front-end
  tests and the script tests. One command, exit code 1 on the first failure, a summary at the end. No arguments.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$web = Join-Path $root 'src\Nexora.Web'
$results = New-Object System.Collections.Generic.List[object]

function Step([string]$Name, [scriptblock]$Body) {
    Write-Host "`n== $Name" -ForegroundColor Cyan
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $global:LASTEXITCODE = 0
    try { & $Body } catch { Write-Host $_.Exception.Message -ForegroundColor Red; $global:LASTEXITCODE = 1 }
    $ok = ($LASTEXITCODE -eq 0)
    $results.Add([pscustomobject]@{ Step = $Name; Result = $(if ($ok) { 'passed' } else { 'FAILED' }); Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1) })
    if (-not $ok) {
        $results | Format-Table -AutoSize | Out-String | Write-Host
        Write-Host "CI failed at: $Name" -ForegroundColor Red
        exit 1
    }
}

Step 'backend build (all warnings are errors)' { dotnet build (Join-Path $root 'Nexora.sln') --no-incremental --nologo -v q }
Step 'TypeScript (tsc --noEmit)' { Push-Location $web; try { npx tsc --noEmit } finally { Pop-Location } }
Step 'ESLint (hooks + accessibility)' { Push-Location $web; try { npx eslint src } finally { Pop-Location } }
Step 'backend tests (Application)' { dotnet test (Join-Path $root 'tests\Nexora.Application.Tests') --no-build --nologo -v q }
Step 'backend tests (API)' { dotnet test (Join-Path $root 'tests\Nexora.Api.Tests') --no-build --nologo -v q }
Step 'front-end tests (vitest)' { Push-Location $web; try { npx vitest run } finally { Pop-Location } }
Step 'script tests' { powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tests\scripts\Test-Scripts.ps1') }

$results | Format-Table -AutoSize | Out-String | Write-Host
Write-Host 'CI passed.' -ForegroundColor Green
exit 0
