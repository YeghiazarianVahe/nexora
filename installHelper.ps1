<#
.SYNOPSIS
  Puts the Nexora helper on one or more PCs, from your PC, so that any operator who signs in there can use it. Nothing is done on the target PC.

.DESCRIPTION
  This is the short form of scripts\deploy-helpers.ps1 (same engine, same safety rules); use that script directly for the full set of options.
    - copies the helper (with the Nexora server key it checks every job against) to <system drive>\ProgramData\Nexora\Helper on the PC,
    - sets up ONE autostart that runs in the session of whoever signs in, with that person's own token: a logon task for the Users group, or
      where Windows refuses that remotely, the all-users Startup shortcut. Never as you, never as SYSTEM,
    - uses the admin share and the remote Task Scheduler / WMI with YOUR rights: no WinRM, no AD or GPO change, nobody has to be signed in.
  The helper is a windowless program; no console flashes at sign-in. It starts at the next sign-in (Windows does not allow starting a program
  in someone else's session remotely). Use -Wait to also wait for already-running helpers to show as connected on the Nexora server.

.EXAMPLE
  .\installHelper.ps1 -Computer WKS-44
.EXAMPLE
  .\installHelper.ps1 -Computer WKS-44, WKS-50 -WhatIf
.EXAMPLE
  .\installHelper.ps1 -Computer WKS-44 -Uninstall
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0, ValueFromPipeline = $true, ValueFromPipelineByPropertyName = $true)]
    [Alias('PCName', 'Name')]
    [string[]]$Computer,

    [string]$Server = 'http://wks-111:5090',

    # Use this helper file instead of downloading one from Nexora (it must carry the server key).
    [string]$HelperPath,

    [switch]$Uninstall,
    [switch]$WhatIf,
    [switch]$PassThru,

    # Wait up to 60 s for running helpers to show as connected on the server.
    [switch]$Wait
)

begin { $names = New-Object System.Collections.Generic.List[string] }
process { foreach ($c in $Computer) { foreach ($n in ($c -split '[,;\s]+')) { if ($n) { $names.Add($n) } } } }
end {
    # run with -File and no pipeline, PowerShell never enters 'process': take the parameter
    if ($names.Count -eq 0 -and $Computer) { foreach ($c in $Computer) { foreach ($n in ($c -split '[,;\s]+')) { if ($n) { $names.Add($n) } } } }
    $deploy = Join-Path $PSScriptRoot 'scripts\deploy-helpers.ps1'
    if (-not (Test-Path -LiteralPath $deploy)) { throw "scripts\deploy-helpers.ps1 was not found next to this script." }
    $splat = @{ Computer = $names.ToArray(); Server = $Server }
    if ($HelperPath) { $splat.HelperPath = $HelperPath }
    if ($Uninstall) { $splat.Uninstall = $true }
    if ($WhatIf) { $splat.WhatIf = $true }
    if ($PassThru) { $splat.PassThru = $true }
    if ($Wait) { $splat.WaitForConnectedSeconds = 60 }
    & $deploy @splat
}
