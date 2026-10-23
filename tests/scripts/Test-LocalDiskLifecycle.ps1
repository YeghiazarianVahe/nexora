$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '..\..\scripts\install-local-disk-telemetry.ps1')
$plan=Get-TelemetryPlan
$task=[pscustomobject]@{Description='Nexora fixed read-only local disk telemetry v1';Actions=@([pscustomobject]@{Execute=$plan.Execute;Arguments=$plan.Arguments});Principal=[pscustomobject]@{UserId='SYSTEM'}}
if(-not(Test-TelemetryTaskOwned $task $plan)){throw 'Owned task was not recognized'}
$task.Actions[0].Arguments+=' -Command malicious'
if(Test-TelemetryTaskOwned $task $plan){throw 'Foreign arguments accepted'}
$task.Actions[0].Arguments=$plan.Arguments; $task.Principal.UserId='DOMAIN\User'
if(Test-TelemetryTaskOwned $task $plan){throw 'Wrong principal accepted'}
function Get-ScheduledTask { throw 'WhatIf touched Task Scheduler' }
foreach($mode in 'Install','Uninstall'){
    $dry=Invoke-TelemetryLifecycle $mode $false
    if($dry.Script -ne $plan.Script){throw 'Dry run changed fixed scope'}
}
Write-Output 'PASS: owned task recognition, hostile arguments, wrong identity, install/uninstall dry runs. No tasks or files modified.'
$acl=New-Object Security.AccessControl.DirectorySecurity
$admin=New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'
$users=New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-545'
$acl.SetOwner($admin)
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($users,'ReadAndExecute','Allow')))
if(-not(Test-TelemetryAclTrusted $acl)){throw 'Read-only users ACL refused'}
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($users,'Write','Allow')))
if(Test-TelemetryAclTrusted $acl){throw 'Untrusted parent write ACL accepted'}
Write-Output 'PASS: trusted owner/read-only users allowed; parent user write rejected. ACLs were constructed only in memory.'
