# Tests for the PowerShell scripts: plain assertions, no Pester needed. Exit code 1 when anything fails.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$failed = 0
function Check([string]$Name, [bool]$Condition) {
    if ($Condition) { Write-Host "  ok   $Name" -ForegroundColor Green }
    else { Write-Host "  FAIL $Name" -ForegroundColor Red; $script:failed++ }
}

Write-Host 'Every script parses'
$skip = '\\(node_modules|bin|obj|publish|run|\.vscode)\\'
foreach ($f in (Get-ChildItem $root -Recurse -Include *.ps1 -File | Where-Object { $_.FullName -notmatch $skip })) {
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$null, [ref]$errors)
    Check ("parses: " + $f.FullName.Substring($root.Length + 1)) (-not $errors)
}

Write-Host 'N6: start/stop only touch the helper of THIS session'
. (Join-Path $root 'nexora.config.ps1')
$mine = [pscustomobject]@{ Id = 101; SessionId = 3 }
$theirs = [pscustomobject]@{ Id = 202; SessionId = 7 }
$own = @(Get-OwnHelperProcess -Process @($mine, $theirs) -SessionId 3)
Check 'two helpers on the PC: only mine is selected' ($own.Count -eq 1 -and $own[0].Id -eq 101)
Check "only another admin's helper running: nothing of mine, so start.ps1 starts mine" (@(Get-OwnHelperProcess -Process @($theirs) -SessionId 3).Count -eq 0)
Check 'nothing running: empty, not an error' (@(Get-OwnHelperProcess -Process @() -SessionId 3).Count -eq 0)
Check 'stop.ps1 uses the filter' ((Get-Content (Join-Path $root 'stop.ps1') -Raw) -match 'Get-OwnHelperProcess')
Check 'start.ps1 uses the filter' ((Get-Content (Join-Path $root 'start.ps1') -Raw) -match 'Get-OwnHelperProcess')

Write-Host 'L10: no password on a command line'
$ops = Get-Content (Join-Path $root 'scripts\nexora.ps1') -Raw
Check 'role creation goes through a protected temp file, not -c' (($ops -match '-f \$sqlFile') -and ($ops -notmatch '-c \$createRole'))
Check 'secret files are protected' ($ops -match 'Protect-SecretFile \$DbConnFile')

Write-Host 'Deploy scripts never use WinRM or a run-as identity'
foreach ($n in 'deploy-helpers.ps1', 'verify-helpers.ps1') {
    $t = Get-Content (Join-Path $root "scripts\$n") -Raw
    Check "$n has no Invoke-Command / Enter-PSSession / New-PSSession" ($t -notmatch '(?m)^[^#\r\n]*\b(Invoke-Command|Enter-PSSession|New-PSSession)\b')
    Check "$n never registers a task as SYSTEM" ($t -notmatch 'S-1-5-18' -and $t -notmatch '/ru\s+SYSTEM')
}
$deploy = Get-Content (Join-Path $root 'scripts\deploy-helpers.ps1') -Raw
Check 'deploy-helpers.ps1 registers the logon task for the Users group only' ($deploy -match 'S-1-5-32-545')

Write-Host 'capture-smart.ps1: read-only, importable, pure helpers'
$capPath = Join-Path $root 'scripts\capture-smart.ps1'
$cap = Get-Content $capPath -Raw
$remotingWords = ('Invoke-' + 'Command'), ('Enter-' + 'PSSession'), ('New-' + 'PSSession'), ('Get-WSMan' + 'Instance'), ('win' + 'rm')
Check 'capture-smart: no remoting cmdlets' ($cap -notmatch ('(?mi)^[^#\r\n]*\b(' + ($remotingWords -join '|') + ')\b'))
Check 'capture-smart: no Install-Module / Import-Module / #requires -Modules' ($cap -notmatch '(?mi)^[^#\r\n]*\b(Install-Module|Import-Module)\b' -and $cap -notmatch '(?i)#requires\s+-modules')
Check 'capture-smart: no write-capable ioctl codes' ($cap -notmatch 'SMART_SEND|SMART_SET|IOCTL_\w*_SET\w*|IOCTL_ATA_PASS_THROUGH|IOCTL_SCSI_PASS_THROUGH|FORMAT_UNIT|0x0007C084|0x0007C08C|0x0007C090|0x002DD')
Check 'capture-smart: no PS7-only syntax' ($cap -notmatch '\?\?|\?\.|-Parallel')
Check 'capture-smart: no self-elevation' ($cap -notmatch '-Verb\s+RunAs')
$helperResult = & {
    . $capPath
    $r = @{}
    $r.hex = ConvertTo-HexString ([byte[]](0, 1, 171, 255))
    $r.hexEmpty = ConvertTo-HexString $null
    $r.name = ConvertTo-SafeFileName 'Micron_2500_MTFDKBA512QGN  '
    $r.nameBad = ConvertTo-SafeFileName '..\ST1000VX012/:*?'
    $r.nameEmpty = ConvertTo-SafeFileName '   '
    $r.nameLong = (ConvertTo-SafeFileName ('A' * 100)).Length
    $r.serial = Get-NormalizedSerial '0025_38B5_1234_5678.'
    $r.pnp = Get-PnpKey 'scsi\disk&ven_x\4&1&0&000000_0'
    $r.bus = Get-BusTypeName 17 'SCSI'
    $r.busFallback = Get-BusTypeName $null 'IDE'
    $r.media = Get-MediaTypeName 4
    $r.mediaNull = Get-MediaTypeName $null
    $r.op = Get-OperationalStatusName @(2)
    $r
}
Check 'hex: lowercase, continuous' ($helperResult.hex -ceq '0001abff')
Check 'hex: null gives empty string' ($helperResult.hexEmpty -ceq '')
Check 'filename: sanitised and lowercase' ($helperResult.name -ceq 'micron-2500-mtfdkba512qgn')
Check 'filename: no path characters' ($helperResult.nameBad -ceq 'st1000vx012')
Check 'filename: blank gives unknown' ($helperResult.nameEmpty -ceq 'unknown')
Check 'filename: capped at 40' ($helperResult.nameLong -eq 40)
Check 'serial join key: letters and digits only, upper case' ($helperResult.serial -ceq '002538B512345678')
Check 'pnp join key: suffix stripped, upper case' ($helperResult.pnp -ceq 'SCSI\DISK&VEN_X\4&1&0&000000')
Check 'bus type: code wins, interface type is the fallback' ($helperResult.bus -ceq 'NVMe' -and $helperResult.busFallback -ceq 'IDE')
Check 'media type: SSD / Unspecified' ($helperResult.media -ceq 'SSD' -and $helperResult.mediaNull -ceq 'Unspecified')
Check 'operational status: OK' ($helperResult.op -ceq 'OK')

Write-Host 'N15: documentation'
$readme = Get-Content (Join-Path $root 'README.md') -Raw
Check 'README has no ".installHelper.ps1" typo' ($readme -notmatch '(?<![\\/A-Za-z])\.installHelper\.ps1')

Write-Host 'F5: diagnostic collectors (static rules)'
$colDir = Join-Path $root 'src\Nexora.Application\Diagnostics\Collectors'
$fixDir = Join-Path $root 'tests\Nexora.Application.Tests\Fixtures\Diagnostics'
$colSpec = [ordered]@{
    'outlook-files'     = @{ top = 'userScope', 'usersScanned', 'files'; items = @{ files = 'user', 'kind', 'path', 'sizeBytes', 'lastWriteUtc', 'onNetworkShare', 'driveLetter', 'source', 'exists' } }
    'outlook-addins'    = @{ top = 'userScope', 'addins', 'resiliency'; items = @{ addins = 'progId', 'description', 'loadBehavior', 'scope', 'wow6432' } }
    'outlook-events'    = @{ top = 'windowDays', 'events', 'counts'; items = @{ events = 'id', 'provider', 'timeUtc', 'messageShort' } }
    'outlook-process'   = @{ top = 'count', 'instances'; items = @{ instances = 'pid', 'workingSetBytes', 'privateBytes', 'handleCount', 'threadCount', 'cpuSeconds', 'responding', 'startTimeUtc', 'sessionId' } }
    'office-version'    = @{ top = , 'installed'; items = @{} }
    'outlook-profile'   = @{ top = 'userScope', 'profiles', 'perUser'; items = @{ perUser = 'user', 'profileCount', 'cachedMode', 'ostCount', 'exchangeCachedModeMonths' } }
    'system-resources'  = @{ top = 'disk', 'memory', 'cpu', 'search'; items = @{} }
    'network-probe'     = @{ top = , 'targets'; items = @{ targets = 'host', 'resolved', 'dnsMs', 'addresses', 'pingSent', 'pingReceived', 'lossPercent', 'minMs', 'avgMs', 'maxMs', 'tcp443' } }
    'pc-slow-processes' = @{ top = 'sampleSeconds', 'logicalProcessors', 'topCpu', 'topMemory'; items = @{ topCpu = 'name', 'pid', 'cpuPercent', 'workingSetBytes'; topMemory = 'name', 'pid', 'cpuPercent', 'workingSetBytes' } }
    'pc-slow-startup'   = @{ top = 'userScope', 'runKeys', 'startupFolderItems', 'runKeysTotal', 'startupFolderTotal', 'autoServicesStopped', 'logonTasksCount', 'uptime'; items = @{ runKeys = 'name', 'command', 'scope' } }
    'pending-reboot'    = @{ top = 'signals', 'pending'; items = @{} }
}
$forbiddenCmd = '^(Import-Module|Invoke-Comm' + 'and|(New|Enter)-PS' + 'Session|Invoke-Expression|iex|Add-Type|Set-(?!StrictMode$).*|Remove-.*|New-Item|Out-File|Add-Content|Set-Content|Start-Process|Stop-.*|Restart-.*|Register-.*|Invoke-WebRequest|Invoke-RestMethod|Install-.*|Enable-.*|Disable-.*)$'
$haveDocs = Get-Content (Join-Path $root 'docs\diagnostics\collector-schemas.md') -Raw
foreach ($id in $colSpec.Keys) {
    $path = Join-Path $colDir "$id.ps1"
    Check "collector exists: $id" (Test-Path $path)
    if (-not (Test-Path $path)) { continue }
    $txt = Get-Content $path -Raw
    $tokens = $null; $perr = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$perr)
    $bad = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true) | ForEach-Object { $_.GetCommandName() } | Where-Object { $_ -and $_ -match $forbiddenCmd })
    Check "${id}: no forbidden/state-changing commands ($($bad -join ','))" ($bad.Count -eq 0)
    Check "${id}: no DllImport / WinRM / remoting text" ($txt -notmatch ('(?i)DllImport|Invoke-Comm' + 'and|PS' + 'Session|-Computer' + 'Name|Get-Credential|UseDefaultCredentials|NetworkCredential'))
    $ps7 = @($tokens | Where-Object { $_.Kind -notin 'Comment', 'StringLiteral', 'StringExpandable', 'HereStringLiteral', 'HereStringExpandable' -and $_.Text -match '^(\?\?|\?\?=|\?\.|\?\[|&&|\|\||\?)$' })
    Check "${id}: no PS7-only syntax" ($ps7.Count -eq 0)
    Check "${id}: declares common fields" (($txt -match 'schema = 1') -and ($txt -match 'collector = \$CollectorId') -and ($txt -match 'collectedUtc') -and ($txt -match 'incomplete = ') -and ($txt -match 'incompleteReason') -and ($txt -match 'errors = '))
    Check "${id}: CollectorId matches file name" ($txt -match ("(?m)^\`$CollectorId = '" + [regex]::Escape($id) + "'"))
    Check "${id}: has -BudgetSeconds default 25 and strict mode" (($txt -match '\[int\]\$BudgetSeconds = 25') -and ($txt -match 'Set-StrictMode -Version Latest') -and ($txt -match "ErrorActionPreference = 'Stop'"))
    Check "${id}: single compact JSON via ConvertTo-Json -Depth 6 -Compress" ($txt -match 'ConvertTo-Json .*-Depth 6 -Compress')
    Check "${id}: documented in collector-schemas.md" ($haveDocs -match [regex]::Escape($id))
}
$parseOut = $null
$env:NX_COL = $colDir
try {
    $parseOut = & powershell.exe -NoProfile -ExecutionPolicy Bypass -Command 'Get-ChildItem -LiteralPath $env:NX_COL -Filter *.ps1 | ForEach-Object { $e = $null; [void][System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$null, [ref]$e); $_.BaseName + [char]124 + @($e).Count }'
} finally { Remove-Item Env:\NX_COL -ErrorAction SilentlyContinue }
foreach ($id in $colSpec.Keys) {
    $line = @($parseOut) | Where-Object { $_ -like "$id|*" }
    Check "${id}: parses without errors under powershell.exe (5.1)" ($line -eq "$id|0")
}

Write-Host 'F5: collector privacy scrubbing'
$np = Get-Content (Join-Path $colDir 'network-probe.ps1') -Raw
Check "network-probe: 'no targets' marks incomplete" ($np -match "reason = 'no targets'")
Check 'outlook-files: path scrubs e-mail' ((Get-Content (Join-Path $colDir 'outlook-files.ps1') -Raw) -match 'Scrub-Path \$path')
Check 'outlook-events: scrubs DOMAIN\user' ((Get-Content (Join-Path $colDir 'outlook-events.ps1') -Raw) -match [regex]::Escape('<domain>\<user>'))
Check 'fixtures: outlook-files paths contain no e-mail' (-not ((Get-Content (Join-Path $fixDir 'outlook-files.positive.json') -Raw) -match '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}'))

Write-Host 'F5: diagnostic fixtures match the schema doc'
function Test-HasKeys($obj, $keys) {
    $names = @($obj.PSObject.Properties | ForEach-Object { $_.Name })
    return @($keys | Where-Object { $names -notcontains $_ })
}
$isoZ = '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$'
foreach ($id in $colSpec.Keys) {
    foreach ($kind in 'positive', 'negative', 'incomplete') {
        $fp = Join-Path $fixDir "$id.$kind.json"
        $label = "fixture ${id}.${kind}"
        if (-not (Test-Path $fp)) { Check "$label exists" $false; continue }
        $raw = Get-Content $fp -Raw
        $j = $null
        try { $j = $raw | ConvertFrom-Json } catch { }
        if ($null -eq $j) { Check "$label is valid JSON" $false; continue }
        $common = Test-HasKeys $j @('schema', 'collector', 'collectedUtc', 'incomplete', 'incompleteReason', 'errors', 'truncated', 'elapsedMs')
        Check "$label has common fields ($($common -join ','))" ($common.Count -eq 0)
        if ($common.Count -gt 0) { continue }
        Check "$label common types" (($j.schema -is [int]) -and $j.schema -eq 1 -and ($j.collector -ceq $id) -and ($j.collectedUtc -match $isoZ) -and ($j.incomplete -is [bool]) -and ($j.truncated -is [bool]) -and ($j.elapsedMs -is [int]) -and ($null -eq $j.incompleteReason -or $j.incompleteReason -is [string]) -and (@($j.errors | Where-Object { $_ -isnot [string] }).Count -eq 0))
        Check "$label incomplete flag matches kind" (($j.incomplete -eq ($kind -eq 'incomplete')) -and ($j.incomplete -eq ($j.incompleteReason -cin $(if ($id -eq 'network-probe') { 'time budget', 'no targets' } else { , 'time budget' }))))
        Check "$label under 20000 chars" ($raw.Length -lt 20000)
        $spec = $colSpec[$id]
        $miss = Test-HasKeys $j $spec.top
        Check "$label has collector fields ($($miss -join ','))" ($miss.Count -eq 0)
        foreach ($listName in $spec.items.Keys) {
            $items = @($j.$listName)
            if ($kind -eq 'positive') { Check "$label ${listName} not empty" ($items.Count -gt 0) }
            foreach ($it in $items) {
                $m = Test-HasKeys $it $spec.items[$listName]
                if ($m.Count -gt 0) { Check "$label ${listName}[] has $($m -join ',')" $false; break }
            }
        }
        if ($id -eq 'outlook-files') {
            Check "$label kind/source enums" (@($j.files | Where-Object { $_.kind -cnotin 'ost', 'pst', 'nst' -or $_.source -cnotin 'filesystem', 'profile', 'both' }).Count -eq 0)
            Check "$label userScope enum" ($j.userScope -cin 'full', 'partial', 'none')
        }
        if ($id -eq 'outlook-events') { Check "$label counts" ($null -eq (Test-HasKeys $j.counts @('crash', 'hang', 'addinSlow', 'dotnetOffice')) -or (Test-HasKeys $j.counts @('crash', 'hang', 'addinSlow', 'dotnetOffice')).Count -eq 0) }
        if ($id -eq 'pending-reboot') { Check "$label signals" ((Test-HasKeys $j.signals @('cbsRebootPending', 'windowsUpdateRebootRequired', 'pendingFileRenameOperations', 'computerRenamePending', 'ccmRebootPending')).Count -eq 0) }
        if ($id -eq 'system-resources') { Check "$label sections" (((Test-HasKeys $j.disk @('systemDriveFreePercent', 'volumes')).Count + (Test-HasKeys $j.memory @('totalBytes', 'freeBytes', 'usedPercent', 'pageFileUsedPercent')).Count + (Test-HasKeys $j.cpu @('loadPercent', 'logicalProcessors')).Count + (Test-HasKeys $j.search @('serviceState', 'startMode', 'indexing')).Count) -eq 0) }
        if ($id -eq 'outlook-addins') { Check "$label resiliency" ((Test-HasKeys $j.resiliency @('disabledItems', 'slowAddins')).Count -eq 0) }
        if ($id -eq 'outlook-profile') { Check "$label profiles object" ((Test-HasKeys $j.profiles @('count', 'cachedMode', 'sharedMailboxesCached', 'additionalMailboxes', 'ostCount', 'exchangeCachedModeMonths')).Count -eq 0) }
        if ($id -eq 'office-version' -and $null -ne $j.installed) { Check "$label installed" ((Test-HasKeys $j.installed @('productName', 'version', 'build', 'channel', 'buildReleaseDateUtc', 'bitness', 'updatesEnabled')).Count -eq 0) }
    }
}

if ($failed -gt 0) { Write-Host "$failed check(s) failed." -ForegroundColor Red; exit 1 }
Write-Host 'All script checks passed.' -ForegroundColor Green
