param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ArgsFromCmd
)

$ErrorActionPreference = 'Stop'

function Write-Info([string]$m) { Write-Host "[INFO] $m" -ForegroundColor Cyan }
function Write-Success([string]$m) { Write-Host "[SUCCESS] $m" -ForegroundColor Green }
function Write-Warn([string]$m) { Write-Host "[WARNING] $m" -ForegroundColor Yellow }
function Write-Err([string]$m) { Write-Host "[ERROR] $m" -ForegroundColor Red }

$BaseDir = Join-Path $env:LOCALAPPDATA 'AxonHub'
$ConfigFile = Join-Path $BaseDir 'config.yml'
$BinaryPath = Join-Path $BaseDir 'axonhub.exe'
$PidFile = Join-Path $BaseDir 'axonhub.pid'
$LogFile = Join-Path $BaseDir 'axonhub.log'
$ErrorLog = Join-Path $BaseDir 'axonhub.err.log'
$DefaultPort = 8090

foreach ($arg in $ArgsFromCmd) {
    switch -Regex ($arg) {
        '^(--help|-h)$' {
            Write-Host @"
Usage: start.bat

Starts AxonHub.NET directly (no service manager).
Config: $ConfigFile
Logs:   $LogFile
PID:    $PidFile
"@
            exit 0
        }
        default { Write-Warn "Unknown option: $arg" }
    }
}

function Ensure-Dirs([string]$path) {
    if (-not (Test-Path $path)) { New-Item -ItemType Directory -Force -Path $path | Out-Null }
}

function Get-ConfiguredPort {
    $port = $DefaultPort
    if (Test-Path -LiteralPath $BinaryPath -PathType Leaf) {
        try {
            Push-Location $BaseDir
            try {
                $value = & $BinaryPath config get server.port 2>$null
                if ($value -match '^[0-9]+$') { $port = [int]$value }
            } finally {
                Pop-Location
            }
        } catch { }
    }
    return $port
}

function Check-Port([int]$port) {
    try {
        $lines = netstat -ano | Select-String -Pattern "^\s*TCP\s+[^:]+:$port\s+"
        if ($lines) {
            Write-Warn "Port $port is already in use"
            $lines | ForEach-Object { Write-Host $_.Line }
            return $false
        }
    } catch { }
    return $true
}

Write-Info 'Starting AxonHub.NET...'
if (-not (Test-Path -LiteralPath $BinaryPath -PathType Leaf)) {
    Write-Err "AxonHub.NET binary not found at $BinaryPath"
    Write-Info 'Run install.bat first.'
    exit 1
}

Ensure-Dirs $BaseDir

if (Test-Path $PidFile) {
    try {
        $processId = Get-Content -Path $PidFile -ErrorAction Stop
        $proc = Get-Process -Id $processId -ErrorAction SilentlyContinue |
            Where-Object { $_.ProcessName -eq 'axonhub' }
        if ($proc) {
            Write-Warn "AxonHub.NET is already running (PID: $processId)"
            exit 0
        }
        Write-Info 'Removing stale PID file'
        Remove-Item -Force $PidFile -ErrorAction SilentlyContinue
    } catch {
        Remove-Item -Force $PidFile -ErrorAction SilentlyContinue
    }
}

$port = Get-ConfiguredPort
if (-not (Check-Port $port)) {
    Write-Err "Cannot start AxonHub.NET: port $port is already in use"
    exit 1
}

if (-not (Test-Path $ConfigFile)) {
    Write-Warn "Configuration not found at $ConfigFile"
    Write-Info 'The server requires PostgreSQL; run install.bat or create config.yml.'
}

if (-not $env:ASPNETCORE_ENVIRONMENT) {
    $env:ASPNETCORE_ENVIRONMENT = 'Production'
}

Write-Info 'Starting AxonHub.NET process...'
try {
    $p = Start-Process -FilePath $BinaryPath `
        -WorkingDirectory $BaseDir `
        -RedirectStandardOutput $LogFile `
        -RedirectStandardError $ErrorLog `
        -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 2
    if ($p -and (Get-Process -Id $p.Id -ErrorAction SilentlyContinue)) {
        $p.Id | Out-File -FilePath $PidFile -Encoding ascii -Force
        Write-Success "AxonHub.NET started (PID: $($p.Id))"
        Write-Host "  PID:    $($p.Id)"
        Write-Host "  Config: $ConfigFile"
        Write-Host "  Log:    $LogFile"
        Write-Host "  Web:    http://localhost:$port"
        Write-Info "Stop with stop.bat; view logs with: Get-Content -Path '$LogFile' -Tail 100 -Wait"
        exit 0
    }

    Write-Err 'AxonHub.NET failed to start'
    if (Test-Path $LogFile) { Get-Content -Path $LogFile -Tail 20 }
    if (Test-Path $ErrorLog) { Get-Content -Path $ErrorLog -Tail 20 }
    Remove-Item -Force $PidFile -ErrorAction SilentlyContinue
    exit 1
} catch {
    Write-Err $_.Exception.Message
    Remove-Item -Force $PidFile -ErrorAction SilentlyContinue
    exit 1
}
