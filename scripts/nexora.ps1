<#
.SYNOPSIS
    Operations script for Nexora | IT Operations Platform (PowerShell 5.1, no external modules).

.EXAMPLE
    .\nexora.ps1 -Action Install      # one time, elevated: URL ACL + firewall rule
    .\nexora.ps1 -Action Build        # dotnet publish (+ frontend build when node is present)
    .\nexora.ps1 -Action DbInit       # one time: private PostgreSQL cluster inside .\run\pgdata
    .\nexora.ps1 -Action Start
    .\nexora.ps1 -Action Status
    .\nexora.ps1 -Action Logs -Tail 100
    .\nexora.ps1 -Action Stop

.NOTES
    Exit codes: 0 = ok, 1 = error.
    All paths are relative to this script's location unless overridden with -InstallPath.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Install', 'Uninstall', 'Build', 'DbInit', 'DbStart', 'DbStop', 'Start', 'Stop', 'Restart', 'Status', 'Logs')]
    [string]$Action,

    [int]$Port = 5090,

    # Folder holding the published Nexora.Api.exe. Default: <repo>\publish
    [string]$InstallPath,

    [int]$Tail = 100,

    # Private PostgreSQL cluster (never touches any other PostgreSQL on this machine).
    [int]$DbPort = 5434,
    [string]$PgBin
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# ---------- paths (all relative to the script) ----------
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Root = Split-Path -Parent $ScriptDir
if (-not $InstallPath) { $InstallPath = Join-Path $Root 'publish' }
$RunDir = Join-Path $Root 'run'
$PidFile = Join-Path $RunDir 'nexora.pid'
$PortFile = Join-Path $RunDir 'nexora.port'
$TokenFile = Join-Path $RunDir 'shutdown.token'
$DbDir = Join-Path $RunDir 'pgdata'
$DbConnFile = Join-Path $RunDir 'db.conn'
$DbSecretFile = Join-Path $RunDir 'db.secret'
$ExeName = 'Nexora.Api.exe'
$Exe = Join-Path $InstallPath $ExeName
$RuleName = "Nexora HTTP $Port"

# ---------- output helpers ----------
function Write-Ok($m)   { Write-Host "[ OK ] $m" -ForegroundColor Green }
function Write-Info($m) { Write-Host "[ .. ] $m" -ForegroundColor Cyan }
function Write-Warn($m) { Write-Host "[WARN] $m" -ForegroundColor Yellow }
function Write-Err($m)  { Write-Host "[FAIL] $m" -ForegroundColor Red }

function Fail($m) {
    Write-Err $m
    exit 1
}

function Test-Elevated {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-CurrentUser { return [Security.Principal.WindowsIdentity]::GetCurrent().Name }

function Ensure-RunDir {
    if (-not (Test-Path $RunDir)) { New-Item -ItemType Directory -Path $RunDir | Out-Null }
}

function New-Secret([int]$bytes) {
    $b = New-Object byte[] $bytes
    $rng = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
    $rng.GetBytes($b)
    $rng.Dispose()
    return ([Convert]::ToBase64String($b) -replace '[^A-Za-z0-9]', '')
}

# ---------- process / port helpers ----------
function Get-NexoraProcess {
    if (-not (Test-Path $PidFile)) { return $null }
    $raw = (Get-Content $PidFile -ErrorAction SilentlyContinue | Select-Object -First 1)
    $id = 0
    if (-not [int]::TryParse("$raw", [ref]$id)) { return $null }
    $p = Get-Process -Id $id -ErrorAction SilentlyContinue
    # Guard against PID reuse: the PID must still be a Nexora.Api process.
    if ($p -and $p.ProcessName -eq [IO.Path]::GetFileNameWithoutExtension($ExeName)) { return $p }
    return $null
}

function Get-PortOwner([int]$p) {
    $c = Get-NetTCPConnection -State Listen -LocalPort $p -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $c) { return $null }
    $owner = Get-Process -Id $c.OwningProcess -ErrorAction SilentlyContinue
    $name = if ($c.OwningProcess -eq 4) { 'HTTP.sys (a web app or URL reservation)' } elseif ($owner) { $owner.ProcessName } else { '?' }
    return [pscustomobject]@{ Pid = $c.OwningProcess; Name = $name }
}

function Get-StoredPort {
    if (Test-Path $PortFile) {
        $v = 0
        if ([int]::TryParse((Get-Content $PortFile | Select-Object -First 1), [ref]$v)) { return $v }
    }
    return $Port
}

function Invoke-Health([int]$p) {
    try {
        $r = Invoke-WebRequest -Uri "http://localhost:$p/health" -UseBasicParsing -TimeoutSec 3
        return ($r.StatusCode -eq 200)
    }
    catch { return $false }
}

# pg_ctl leaves the server holding its stdout/stderr (and Start-Process -Wait also waits for descendants),
# so: redirect to files, never pipe, wait only for pg_ctl itself, and poll the port when starting.
function Invoke-PgCtl([string[]]$pgArgs, [int]$timeoutMs = 60000) {
    $pgctl = Get-PgTool 'pg_ctl'
    $out = Join-Path $RunDir 'pg_ctl.out'
    $err = Join-Path $RunDir 'pg_ctl.err'
    $quoted = ($pgArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
    # Give PostgreSQL its own hidden console. Sharing the launching terminal lets a
    # Ctrl+C / terminal close stop the database while the detached API keeps running.
    $p = Start-Process -FilePath $pgctl -ArgumentList $quoted -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $out -RedirectStandardError $err
    $null = $p.Handle   # caches the handle so ExitCode is not null after exit (Start-Process quirk)
    if (-not $p.WaitForExit($timeoutMs)) { try { $p.Kill() } catch { } ; return 1 }
    return $p.ExitCode
}

function Start-PgServer {
    $rc = Invoke-PgCtl @("-D", $DbDir, "-l", (Join-Path $RunDir "pg.log"), "start")
    if ($rc -ne 0) { Fail "pg_ctl start failed; see $RunDir\pg_ctl.err" }
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        if (Get-PortOwner $DbPort) { return }
        Start-Sleep -Milliseconds 500
    }
    Fail "PostgreSQL did not start listening on $DbPort; see $RunDir\pg.log"
}

function Get-PgTool([string]$name) {
    $dirs = @()
    if ($PgBin) { $dirs += $PgBin }
    $found = Get-ChildItem 'C:\Program Files\PostgreSQL' -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending
    foreach ($d in $found) { $dirs += (Join-Path $d.FullName 'bin') }
    foreach ($d in $dirs) {
        $candidate = Join-Path $d "$name.exe"
        if (Test-Path $candidate) { return $candidate }
    }
    Fail "PostgreSQL tool '$name' not found. Install PostgreSQL 16+ or pass -PgBin <folder>."
}

# ---------- actions ----------
function Do-Install {
    if (-not (Test-Elevated)) { Fail 'Install must run elevated (Run as administrator).' }
    & (Join-Path $ScriptDir 'install-local-disk-telemetry.ps1') -Action Install
    $user = Get-CurrentUser
    $url = "http://+:$Port/"

    Write-Info "URL reservation $url for $user"
    $existing = (netsh http show urlacl url=$url) -join "`n"
    if ($existing -match 'Reserved URL') {
        if ($existing -match [regex]::Escape($user)) { Write-Ok 'URL reservation already present' }
        else { Fail "URL $url is reserved for a different user. Remove it first: netsh http delete urlacl url=$url" }
    }
    else {
        $out = (netsh http add urlacl url=$url user=$user) -join ' '
        if ($LASTEXITCODE -ne 0) { Fail "netsh urlacl failed: $out" }
        Write-Ok 'URL reservation added'
    }

    Write-Info "Firewall rule '$RuleName' (TCP $Port, Domain profile)"
    $rule = (netsh advfirewall firewall show rule name="$RuleName") -join "`n"
    if ($rule -match 'Rule Name') { Write-Ok 'Firewall rule already present' }
    else {
        $out = (netsh advfirewall firewall add rule name="$RuleName" dir=in action=allow protocol=TCP localport=$Port profile=domain) -join ' '
        if ($LASTEXITCODE -ne 0) { Fail "netsh firewall failed: $out" }
        Write-Ok 'Firewall rule added'
    }

    $owner = Get-PortOwner $Port
    if ($owner -and -not (Get-NexoraProcess)) {
        Write-Warn "Port $Port is currently used by $($owner.Name) (PID $($owner.Pid)). Pick another with -Port or stop it before Start."
    }
}

function Do-Uninstall {
    if (-not (Test-Elevated)) { Fail 'Uninstall must run elevated (Run as administrator).' }
    & (Join-Path $ScriptDir 'install-local-disk-telemetry.ps1') -Action Uninstall
    $url = "http://+:$Port/"
    $existing = (netsh http show urlacl url=$url) -join "`n"
    if ($existing -match 'Reserved URL') {
        netsh http delete urlacl url=$url | Out-Null
        if ($LASTEXITCODE -ne 0) { Fail 'Could not delete the URL reservation.' }
        Write-Ok 'URL reservation removed'
    }
    else { Write-Info 'No URL reservation to remove' }

    $rule = (netsh advfirewall firewall show rule name="$RuleName") -join "`n"
    if ($rule -match 'Rule Name') {
        netsh advfirewall firewall delete rule name="$RuleName" | Out-Null
        Write-Ok 'Firewall rule removed'
    }
    else { Write-Info 'No firewall rule to remove' }
}

function Do-Build {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) { Fail 'dotnet SDK not found on PATH.' }
    $web = Join-Path $Root 'src\Nexora.Web'
    if ((Test-Path (Join-Path $web 'package.json')) -and (Get-Command npm -ErrorAction SilentlyContinue)) {
        Write-Info 'Building frontend'
        Push-Location $web
        try {
            if (-not (Test-Path 'node_modules')) { npm install --no-audit --no-fund; if ($LASTEXITCODE -ne 0) { Fail 'npm install failed' } }
            npm run build
            if ($LASTEXITCODE -ne 0) { Fail 'frontend build failed' }
        }
        finally { Pop-Location }
    }
    else { Write-Warn 'Frontend not built (no node/npm or no package.json); the API will serve no UI.' }

    Write-Info "Publishing to $InstallPath"
    dotnet publish (Join-Path $Root 'src\Nexora.Api\Nexora.Api.csproj') -c Release -o $InstallPath --nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail 'dotnet publish failed' }
    Write-Ok "Published $ExeName"

    $helperOut = Join-Path $InstallPath 'helper'
    Write-Info "Publishing the operator helper to $helperOut"
    # Self-contained single file: an operator PC needs no .NET installed to run the helper.
    dotnet publish (Join-Path $Root 'src\Nexora.Helper\Nexora.Helper.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $helperOut --nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail 'helper publish failed' }
    Write-Ok 'Published Nexora.Helper.exe (self-contained; operators download it from the Jobs page of Nexora)'
}

function Do-DbInit {
    Ensure-RunDir
    if (Test-Path $DbDir) { Fail "A private cluster already exists at $DbDir. Refusing to touch it." }
    if (Get-PortOwner $DbPort) { Fail "Port $DbPort is already in use. Choose another with -DbPort." }

    $initdb = Get-PgTool 'initdb'
    $psql = Get-PgTool 'psql'

    $superPw = New-Secret 24
    $appPw = New-Secret 24
    $pwFile = Join-Path $RunDir 'pg.pwfile'
    Set-Content -Path $pwFile -Value $superPw -Encoding ASCII

    Write-Info "Creating private PostgreSQL cluster in $DbDir (port $DbPort, localhost only)"
    & $initdb -D $DbDir -U nexora_pg --auth=scram-sha-256 --pwfile=$pwFile -E UTF8 | Out-Null
    $initOk = ($LASTEXITCODE -eq 0)
    Remove-Item $pwFile -Force -ErrorAction SilentlyContinue
    if (-not $initOk) { Fail 'initdb failed' }

    Add-Content -Path (Join-Path $DbDir 'postgresql.conf') -Value "`nport = $DbPort`nlisten_addresses = '127.0.0.1'`n"

    Start-PgServer

    # The application password must not appear on a command line (any user on this PC can read process arguments): the SQL goes through a
    # temporary file that only the current user can read, and the superuser password through the environment of this one process.
    $env:PGPASSWORD = $superPw
    $sqlFile = Join-Path ([IO.Path]::GetTempPath()) ("nexora-init-" + [guid]::NewGuid().ToString('N') + '.sql')
    try {
        Set-Content -Path $sqlFile -Value @("CREATE ROLE nexora_app LOGIN PASSWORD '$appPw';", 'CREATE DATABASE nexora OWNER nexora_app;') -Encoding ASCII
        Protect-SecretFile $sqlFile
        & $psql -h 127.0.0.1 -p $DbPort -U nexora_pg -d postgres -v ON_ERROR_STOP=1 -q -f $sqlFile
        if ($LASTEXITCODE -ne 0) { Fail 'Creating the nexora role/database failed' }
    }
    finally {
        Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
        Remove-Item $sqlFile -Force -ErrorAction SilentlyContinue
    }

    Set-Content -Path $DbSecretFile -Value "superuser nexora_pg : $superPw" -Encoding ASCII
    Set-Content -Path $DbConnFile -Value "Host=127.0.0.1;Port=$DbPort;Database=nexora;Username=nexora_app;Password=$appPw" -Encoding ASCII
    Protect-SecretFile $DbSecretFile
    Protect-SecretFile $DbConnFile
    Write-Ok "Database ready. Connection string stored in $DbConnFile (kept out of git)."
}

# Only the current user (and SYSTEM) may read a file that holds a password; inherited permissions are removed.
function Protect-SecretFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    & icacls.exe $Path /inheritance:r /grant:r "$($env:USERDOMAIN)\$($env:USERNAME):(R,W)" 'NT AUTHORITY\SYSTEM:(F)' | Out-Null
}

function Do-DbStart {
    if (-not (Test-Path $DbDir)) { Fail 'No private cluster. Run -Action DbInit first.' }
    Protect-SecretFile $DbSecretFile   # also tightens files written by an older version
    Protect-SecretFile $DbConnFile
    if (Get-PortOwner $DbPort) { Write-Ok "PostgreSQL already listening on $DbPort"; return }
    Start-PgServer
    Write-Ok "PostgreSQL started on $DbPort"
}

function Do-DbStop {
    if (Get-NexoraProcess) { Fail 'Stop Nexora with -Action Stop before stopping its database. Otherwise API requests fail with Internal Server Error.' }
    if (-not (Test-Path $DbDir)) { Write-Info 'No private cluster'; return }
    [void](Invoke-PgCtl @("-D", $DbDir, "-m", "fast", "-w", "stop"))
    Write-Ok 'PostgreSQL stopped'
}

function Do-Start {
    Ensure-RunDir
    if (Get-NexoraProcess) { Fail "Nexora is already running (PID $((Get-NexoraProcess).Id)). Use -Action Restart." }
    if (Test-Path $PidFile) { Write-Warn 'Removing stale PID file'; Remove-Item $PidFile -Force }
    if (-not (Test-Path $Exe)) { Fail "$Exe not found. Run -Action Build first (or pass -InstallPath)." }

    $owner = Get-PortOwner $Port
    if ($owner) { Fail "Port $Port is already in use by $($owner.Name) (PID $($owner.Pid)). Use -Port <other> or stop that process." }

    # Start the private database if there is one and it is down.
    if ((Test-Path $DbDir) -and -not (Get-PortOwner $DbPort)) { Do-DbStart }

    $token = New-Secret 32
    Set-Content -Path $TokenFile -Value $token -Encoding ASCII
    Set-Content -Path $PortFile -Value $Port -Encoding ASCII

    $env:Nexora__Url = "http://+:$Port/"
    $env:Nexora__ShutdownToken = $token
    if (Test-Path $DbConnFile) { $env:ConnectionStrings__Nexora = (Get-Content $DbConnFile | Select-Object -First 1) }

    Write-Info "Starting $ExeName on port $Port as $(Get-CurrentUser)"
    $proc = Start-Process -FilePath $Exe -WorkingDirectory $InstallPath -WindowStyle Hidden -PassThru
    $null = $proc.Handle   # keep the handle so ExitCode is readable if it dies early
    Set-Content -Path $PidFile -Value $proc.Id -Encoding ASCII

    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) {
            Remove-Item $PidFile -Force -ErrorAction SilentlyContinue
            Write-Err "Nexora exited during startup (exit code $($proc.ExitCode)). Last log lines:"
            Show-Logs 15
            exit 1
        }
        if (Invoke-Health $Port) {
            Write-Ok "Nexora is up (PID $($proc.Id))"
            Write-Host "       http://$($env:COMPUTERNAME.ToLower()):$Port" -ForegroundColor White
            return
        }
        Start-Sleep -Milliseconds 500
    }
    Write-Err 'Nexora did not become healthy within 30 s. Last log lines:'
    Show-Logs 15
    exit 1
}

function Do-Stop {
    $p = Get-NexoraProcess
    if (-not $p) {
        if (Test-Path $PidFile) { Write-Warn 'Stale PID file removed'; Remove-Item $PidFile -Force }
        else { Write-Info 'Nexora is not running' }
        return
    }

    $port = Get-StoredPort
    $stopped = $false
    if (Test-Path $TokenFile) {
        try {
            $token = (Get-Content $TokenFile | Select-Object -First 1)
            Invoke-WebRequest -Uri "http://127.0.0.1:$port/internal/shutdown" -Method Post -UseBasicParsing `
                -Headers @{ 'X-Nexora-Shutdown' = $token } -TimeoutSec 5 | Out-Null
            Write-Info 'Graceful shutdown requested'
            $stopped = $p.WaitForExit(15000)
        }
        catch { Write-Warn "Graceful shutdown request failed: $($_.Exception.Message)" }
    }
    if (-not $stopped) {
        Write-Warn 'Forcing the process to stop'
        Stop-Process -Id $p.Id -Force
        $p.WaitForExit(5000) | Out-Null
    }
    Remove-Item $PidFile, $PortFile, $TokenFile -Force -ErrorAction SilentlyContinue
    Write-Ok 'Nexora stopped'
}

function Do-Status {
    try { & (Join-Path $ScriptDir 'install-local-disk-telemetry.ps1') -Action Status | Format-List } catch { Write-Warn 'Local disk collector health unavailable; check from an elevated session.' }
    $p = Get-NexoraProcess
    $port = Get-StoredPort
    if (-not $p) {
        Write-Host 'Status      : ' -NoNewline; Write-Host 'STOPPED' -ForegroundColor Red
        if (Test-Path $PidFile) { Write-Warn 'Stale PID file present (Start will clean it up)' }
        $owner = Get-PortOwner $Port
        if ($owner) { Write-Warn "Port $Port is used by $($owner.Name) (PID $($owner.Pid))" }
        exit 1
    }
    $uptime = (Get-Date) - $p.StartTime
    Write-Host 'Status      : ' -NoNewline; Write-Host 'RUNNING' -ForegroundColor Green
    Write-Host ("PID         : {0}" -f $p.Id)
    Write-Host ("Uptime      : {0:d\.hh\:mm\:ss}" -f $uptime)
    Write-Host ("Memory      : {0:N0} MB" -f ($p.WorkingSet64 / 1MB))
    $owner = Get-PortOwner $port
    # HTTP.sys owns the socket (PID 4) on behalf of the process
    if ($owner -and ($owner.Pid -eq $p.Id -or $owner.Pid -eq 4)) { Write-Host "Port $port   : " -NoNewline; Write-Host 'listening' -ForegroundColor Green }
    else { Write-Host "Port $port   : " -NoNewline; Write-Host 'NOT listening' -ForegroundColor Red }
    $healthy = Invoke-Health $port
    Write-Host 'Health      : ' -NoNewline
    if ($healthy) { Write-Host 'OK' -ForegroundColor Green } else { Write-Host 'FAILED' -ForegroundColor Red; exit 1 }
    Write-Host ("URL         : http://{0}:{1}" -f $env:COMPUTERNAME.ToLower(), $port)
}

function Show-Logs([int]$n) {
    $logDir = Join-Path $InstallPath 'logs'
    $latest = Get-ChildItem $logDir -Filter 'nexora-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $latest) { Write-Warn "No log files in $logDir"; return }
    Write-Info $latest.FullName
    Get-Content $latest.FullName -Tail $n
}

# ---------- dispatch ----------
switch ($Action) {
    'Install'   { Do-Install }
    'Uninstall' { Do-Uninstall }
    'Build'     { Do-Build }
    'DbInit'    { Do-DbInit }
    'DbStart'   { Do-DbStart }
    'DbStop'    { Do-DbStop }
    'Start'     { Do-Start }
    'Stop'      { Do-Stop }
    'Restart'   { Do-Stop; Do-Start }
    'Status'    { Do-Status }
    'Logs'      { Show-Logs $Tail }
}
exit 0
