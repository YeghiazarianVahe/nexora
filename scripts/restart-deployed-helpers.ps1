[CmdletBinding()]
param([string[]]$Computer = @('wks-50', 'wks-007', 'wks-36', 'wks-44', 'wks-201', 'wks-122', 'wks-75', 'wks-59', 'wks-51', 'wks-25', 'wks-13'))
$ErrorActionPreference = 'Stop'
foreach ($pc in $Computer) {
    $cim = $null
    try {
        $svc = New-Object -ComObject Schedule.Service
        $svc.Connect($pc)
        $task = $svc.GetFolder('\').GetTask('Nexora Helper')
        if ($task.Xml -notmatch 'C:\\ProgramData\\Nexora\\Helper\\Nexora.Helper.exe') { throw 'Unexpected helper task path.' }
        $sessionIds = @()
        $q = & quser.exe /server:$pc
        if ($LASTEXITCODE -ne 0) { throw 'Cannot read signed-in sessions.' }
        foreach ($line in ($q | Select-Object -Skip 1)) {
            if ($line -match '^\s*>?.+?\s+(\d+)\s+\S+\s+') { $sessionIds += [int]$Matches[1] }
        }
        if ($sessionIds.Count -eq 0) { throw 'No signed-in sessions found; existing processes were left alone.' }
        $cim = New-CimSession -ComputerName $pc -SessionOption (New-CimSessionOption -Protocol Dcom) -OperationTimeoutSec 20
        foreach ($process in @(Get-CimInstance -CimSession $cim -ClassName Win32_Process -Filter "Name='Nexora.Helper.exe'")) {
            $result = Invoke-CimMethod -CimSession $cim -InputObject $process -MethodName Terminate
            if ($result.ReturnValue -ne 0) { throw "Could not stop helper $($process.ProcessId): $($result.ReturnValue)" }
        }
        Start-Sleep -Milliseconds 500
        foreach ($sessionId in ($sessionIds | Select-Object -Unique)) {
            if ($sessionId -gt 0) { [void]$task.RunEx($null, 4, $sessionId, $null) }
        }
        Write-Output "$pc : restarted updated helper in session(s) $($sessionIds -join ', ')"
    }
    catch { Write-Output "$pc : FAILED: $($_.Exception.Message)" }
    finally { if ($cim) { Remove-CimSession $cim } }
}
