<#
.SYNOPSIS
    Configures Firefox for Windows SSO against Nexora by writing per-user prefs to user.js
    in every Firefox profile of the current user. PowerShell 5.1, no external modules.

.EXAMPLE
    .\configure-firefox.ps1                       # trusts "wks-111"
    .\configure-firefox.ps1 -HostName nexora-srv  # another short host name
    .\configure-firefox.ps1 -Remove               # take the prefs back out

    Close Firefox first (it rewrites prefs on exit), then start it again.
    For fleet-wide setup use policies.json instead (see README).
#>
[CmdletBinding()]
param(
    [string]$HostName = 'wks-111',
    [switch]$Remove
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if ($HostName -notmatch '^[A-Za-z0-9][A-Za-z0-9.\-]*$') {
    Write-Host "[FAIL] Invalid host name '$HostName'" -ForegroundColor Red
    exit 1
}

if (Get-Process firefox -ErrorAction SilentlyContinue) {
    Write-Host '[WARN] Firefox is running; close it first or it will overwrite these settings on exit.' -ForegroundColor Yellow
}

$profilesRoot = Join-Path $env:APPDATA 'Mozilla\Firefox\Profiles'
if (-not (Test-Path $profilesRoot)) {
    Write-Host "[FAIL] No Firefox profiles found at $profilesRoot" -ForegroundColor Red
    exit 1
}

$begin = '// BEGIN Nexora SSO'
$end = '// END Nexora SSO'
$block = @(
    $begin,
    ('user_pref("network.negotiate-auth.trusted-uris", "{0}");' -f $HostName),
    ('user_pref("network.automatic-ntlm-auth.trusted-uris", "{0}");' -f $HostName),
    $end
) -join "`r`n"

function Remove-Block([string]$text) {
    $pattern = '(?s)' + [regex]::Escape($begin) + '.*?' + [regex]::Escape($end) + '\r?\n?'
    return [regex]::Replace($text, $pattern, '')
}

$count = 0
foreach ($profile in Get-ChildItem $profilesRoot -Directory) {
    $file = Join-Path $profile.FullName 'user.js'
    $text = ''
    if (Test-Path $file) { $text = [IO.File]::ReadAllText($file) }
    $clean = Remove-Block $text
    if ($Remove) {
        if ($clean -ne $text) { [IO.File]::WriteAllText($file, $clean); $count++ }
    }
    else {
        $new = $clean.TrimEnd() + "`r`n" + $block + "`r`n"
        [IO.File]::WriteAllText($file, $new.TrimStart())
        $count++
    }
}

if ($Remove) { Write-Host "[ OK ] Removed Nexora SSO prefs from $count profile(s)." -ForegroundColor Green }
else { Write-Host "[ OK ] Trusted '$HostName' for Negotiate/NTLM in $count profile(s). Restart Firefox." -ForegroundColor Green }
exit 0
