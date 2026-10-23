# Settings shared by start.ps1, stop.ps1 and restart.ps1. Edit here; the scripts take no arguments.

# Port Nexora listens on. The pilot runs on 5090 (5080 belongs to the Service Desk Console on this PC).
# The helper, the app, the README and the scripts all default to 5090; change it here and pass --server to helpers elsewhere.
$NexoraPort = 5090

# Also start/stop the operator helper (Nexora.Helper) in your own Windows session.
# On a server PC that nobody operates from, set this to $false.
$ManageHelper = $true

# The helper processes that belong to YOU: the ones running in your own Windows session. With the all-users startup, other admins' helpers
# run on this PC too (in their sessions); start.ps1/stop.ps1 must neither skip starting yours because of theirs nor kill theirs.
function Get-OwnHelperProcess {
    param($Process = (Get-Process -Name 'Nexora.Helper' -ErrorAction SilentlyContinue), [int]$SessionId = (Get-Process -Id $PID).SessionId)
    @($Process | Where-Object { $_.SessionId -eq $SessionId })
}
