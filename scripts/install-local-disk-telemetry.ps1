[CmdletBinding(SupportsShouldProcess)]
param([ValidateSet('Install','Status','Uninstall')][string]$Action='Install')
$ErrorActionPreference='Stop'
function Get-TelemetryPlan {
    $directory=Join-Path (Join-Path $env:ProgramData 'Nexora') 'DiskTelemetry'
    $scriptPath=Join-Path $directory 'collect.ps1'
    [pscustomobject]@{Name='Nexora-LocalDiskTelemetry';Directory=$directory;Script=$scriptPath;Output=(Join-Path $directory 'result.json');
        Execute="$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe";
        Arguments=('-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "'+$scriptPath+'"')}
}
function Test-TelemetryTaskOwned($Task,$Plan) {
    $actions=@($Task.Actions)
    return ($Task.Description -in '','Nexora fixed read-only local disk telemetry v1') -and $actions.Count -eq 1 -and
        $actions[0].Execute -eq $Plan.Execute -and $actions[0].Arguments -eq $Plan.Arguments -and $Task.Principal.UserId -in 'SYSTEM','S-1-5-18'
}
function Assert-TelemetryDirectory([string]$Path) {
    if(-not(Test-Path -LiteralPath $Path)){New-Item -ItemType Directory -Path $Path | Out-Null}
    if((Get-Item -LiteralPath $Path).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked telemetry directory refused.'}
    $owner=(Get-Acl -LiteralPath $Path).GetOwner([Security.Principal.SecurityIdentifier]).Value
    $current=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    if($owner -notin 'S-1-5-32-544','S-1-5-18',$current){throw 'Untrusted telemetry directory owner.'}
}
function Test-TelemetryAclTrusted($Acl) {
    if($Acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin 'S-1-5-32-544','S-1-5-18'){return $false}
    $writes=[Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
    foreach($rule in $Acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){
        if($rule.AccessControlType -eq 'Allow' -and ($rule.FileSystemRights -band $writes) -and $rule.IdentityReference.Value -notin 'S-1-5-32-544','S-1-5-18'){return $false}
    }
    return $true
}
function Invoke-TelemetryLifecycle([string]$Mode,[bool]$Apply) {
    $plan=Get-TelemetryPlan
    if(-not $Apply -and $Mode -ne 'Status'){return $plan}
    $task=$null; $schedulerError=$null
    try{$task=Get-ScheduledTask -TaskName $plan.Name -ErrorAction Stop}
    catch{
        if($_.CategoryInfo.Category -ne 'ObjectNotFound'){
            if($Mode -ne 'Status'){throw}
            $schedulerError='Task Scheduler state unavailable; check from an elevated session.'
        }
    }
    if($task -and -not(Test-TelemetryTaskOwned $task $plan)){throw 'Task name belongs to an unrelated action; refusing to modify it.'}
    if($Mode -eq 'Status'){
        $sample=$null; try{$sample=Get-Content -LiteralPath $plan.Output -Raw -ErrorAction Stop|ConvertFrom-Json}catch{}
        $info=$null; if($task){$info=Get-ScheduledTaskInfo -TaskName $plan.Name}
        return [pscustomobject]@{Installed=$(if($schedulerError){$null}else{$null -ne $task});SchedulerStateAvailable=($null -eq $schedulerError);Error=$schedulerError;State=$task.State;LastResult=$info.LastTaskResult;LastRun=$info.LastRunTime;
            CollectedUtc=$sample.CollectedUtc;Fresh=($sample.CollectedUtc -and ([DateTime]::UtcNow-[DateTime]$sample.CollectedUtc).TotalMinutes -lt 2 -and [DateTime]$sample.CollectedUtc -le [DateTime]::UtcNow.AddSeconds(30))}
    }
    $id=[Security.Principal.WindowsIdentity]::GetCurrent()
    if(-not([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Telemetry installation/removal requires administrator rights.'}
    if($task -and $task.State -eq 'Running'){
        Stop-ScheduledTask -TaskName $plan.Name
        $deadline=[DateTime]::UtcNow.AddSeconds(10)
        do{Start-Sleep -Milliseconds 200; $task=Get-ScheduledTask -TaskName $plan.Name}while($task.State -eq 'Running' -and [DateTime]::UtcNow -lt $deadline)
        if($task.State -eq 'Running'){throw 'Telemetry task did not stop; refusing to replace its script.'}
    }
    if($Mode -eq 'Uninstall'){
        $marker=Join-Path $plan.Directory '.nexora-telemetry-v1'
        if(-not $task -and (-not(Test-Path -LiteralPath $marker) -or (Get-Content -LiteralPath $marker -Raw).Trim() -ne 'Nexora fixed telemetry v1')){return}
        if($task){Unregister-ScheduledTask -TaskName $plan.Name -Confirm:$false}
        if(Test-Path -LiteralPath $plan.Directory){
            Assert-TelemetryDirectory $plan.Directory
            foreach($name in 'collect.ps1','collect.ps1.new','result.json','result.json.tmp','.nexora-telemetry-v1'){
                $path=Join-Path $plan.Directory $name
                if(Test-Path -LiteralPath $path){if((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked telemetry file refused.'}; Remove-Item -LiteralPath $path -Force}
            }
            if(@(Get-ChildItem -LiteralPath $plan.Directory -Force).Count -eq 0){Remove-Item -LiteralPath $plan.Directory}
        }
        return
    }
    $root=Split-Path -Parent $plan.Directory
    $newRoot=-not(Test-Path -LiteralPath $root)
    Assert-TelemetryDirectory $root; Assert-TelemetryDirectory $plan.Directory
    $admins=New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'
    # Never expand or silently replace existing shared-root permissions.
    $acl=New-Object Security.AccessControl.DirectorySecurity
    $acl.SetOwner($admins); $acl.SetAccessRuleProtection($true,$false)
    foreach($sid in 'S-1-5-32-544','S-1-5-18'){$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier $sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow')))}
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-545'),'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow')))
    if($newRoot){Set-Acl -LiteralPath $root -AclObject $acl}
    elseif(-not(Test-TelemetryAclTrusted (Get-Acl -LiteralPath $root))){throw 'Shared Nexora root is writable/owned by an untrusted principal. Prepare a protected root before installing SYSTEM telemetry.'}
    Set-Acl -LiteralPath $plan.Directory -AclObject $acl
    $template=Join-Path $PSScriptRoot 'native-disk-telemetry.ps1'
    if(-not(Test-Path -LiteralPath $template)){$template=Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Nexora.Infrastructure\Wmi\native-nvme-read.ps1'}
    $body=(Get-Content -LiteralPath $template -Raw).Replace('@@INDEXES@@',((0..31)-join ',')).Replace('@@OUTPUT@@',$plan.Output)
    foreach($path in @($plan.Script,($plan.Script+'.new'))){if((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked telemetry script refused.'}}
    [IO.File]::WriteAllText(($plan.Script+'.new'),$body,(New-Object Text.UTF8Encoding($false)))
    $fileAcl=Get-Acl -LiteralPath ($plan.Script+'.new'); $fileAcl.SetOwner($admins); Set-Acl -LiteralPath ($plan.Script+'.new') -AclObject $fileAcl
    Move-Item -LiteralPath ($plan.Script+'.new') -Destination $plan.Script -Force
    $marker=Join-Path $plan.Directory '.nexora-telemetry-v1'
    if((Test-Path -LiteralPath $marker) -and ((Get-Item -LiteralPath $marker).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked telemetry marker refused.'}
    Set-Content -LiteralPath $marker -Value 'Nexora fixed telemetry v1' -Encoding ASCII
    $action=New-ScheduledTaskAction -Execute $plan.Execute -Argument $plan.Arguments
    $trigger=New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1)
    $settings=New-ScheduledTaskSettingsSet -Hidden -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 1) -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName $plan.Name -Description 'Nexora fixed read-only local disk telemetry v1' -Action $action -Trigger $trigger -Settings $settings -User SYSTEM -RunLevel Highest -Force|Out-Null
    Start-ScheduledTask -TaskName $plan.Name
}
if($MyInvocation.InvocationName -ne '.'){
    Invoke-TelemetryLifecycle $Action ($Action -eq 'Status' -or $PSCmdlet.ShouldProcess('Nexora local disk collector',$Action))
}
