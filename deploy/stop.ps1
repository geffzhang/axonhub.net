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
$PidFile = Join-Path $BaseDir 'axonhub.pid'

$Force = $false
foreach ($arg in $ArgsFromCmd) {
    switch -Regex ($arg) {
        '^(--force)$' { $Force = $true; continue }
        '^(--help|-h)$' {
            Write-Host @'
Usage: stop.bat [--force]

Stops AxonHub.NET directly (no service manager).
Options:
  --force     Force kill all AxonHub.NET processes
'@
            exit 0
        }
        default { Write-Warn "Unknown option: $arg" }
    }
}

function Stop-ByPid {
    Write-Info 'Stopping AxonHub.NET using PID file...'
    if (-not (Test-Path $PidFile)) {
        Write-Warn "PID file not found at $PidFile"
        return $false
    }
    try {
        $processId = Get-Content -Path $PidFile -ErrorAction Stop
    } catch {
        Write-Warn 'Unable to read PID file'
        Remove-Item -Force $PidFile -ErrorAction SilentlyContinue
        return $false
    }
    if (-not ($processId -match '^[0-9]+$')) {
        Write-Err "Invalid PID in file: $processId"
        Remove-Item -Force $PidFile -ErrorAction SilentlyContinue
        return $false
    }
    $proc = Get-Process -Id $processId -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -eq 'axonhub' }
    if (-not $proc) {
        Write-Warn "PID $processId is not AxonHub.NET (PID may have been reused)"
        Remove-Item -Force $PidFile -ErrorAction SilentlyContinue
        return $false
    }
    Write-Info "Stopping process $processId ..."
    try { Stop-Process -Id $processId -ErrorAction SilentlyContinue } catch { }
    for ($i = 0; $i -lt 15; $i++) {
        Start-Sleep -Seconds 1
        if (-not (Get-Process -Id $processId -ErrorAction SilentlyContinue)) { break }
    }
    if (Get-Process -Id $processId -ErrorAction SilentlyContinue) {
        Write-Warn 'Process did not stop gracefully; forcing termination'
        try { Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue } catch { }
        Start-Sleep -Seconds 2
    }
    if (-not (Get-Process -Id $processId -ErrorAction SilentlyContinue)) {
        Write-Success "AxonHub.NET stopped (PID: $processId)"
        Remove-Item -Force $PidFile -ErrorAction SilentlyContinue
        return $true
    }
    Write-Err 'Failed to stop AxonHub.NET process'
    return $false
}

function Find-AxonHubProcesses {
    Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -eq 'axonhub' }
}

function Stop-ByProcessName {
    Write-Info 'Stopping AxonHub.NET by process name...'
    $procs = Find-AxonHubProcesses
    if (-not $procs) {
        Write-Warn 'No AxonHub.NET processes found'
        return $false
    }
    Write-Info ("Found AxonHub.NET processes: " + ($procs.Id -join ' '))
    foreach ($p in $procs) {
        try { Stop-Process -Id $p.Id -ErrorAction SilentlyContinue } catch { }
        for ($i = 0; $i -lt 15; $i++) {
            Start-Sleep -Seconds 1
            if (-not (Get-Process -Id $p.Id -ErrorAction SilentlyContinue)) { break }
        }
        if (Get-Process -Id $p.Id -ErrorAction SilentlyContinue) {
            Write-Warn "Process $($p.Id) did not stop gracefully; forcing"
            try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
        }
    }
    Start-Sleep -Seconds 2
    if (-not (Find-AxonHubProcesses)) {
        Write-Success 'All AxonHub.NET processes stopped'
        Remove-Item -Force $PidFile -ErrorAction SilentlyContinue
        return $true
    }
    Write-Err 'Some AxonHub.NET processes are still running'
    return $false
}

if ($Force) {
    Write-Info 'Force stopping all AxonHub.NET processes...'
    $procs = Find-AxonHubProcesses
    if ($procs) {
        foreach ($p in $procs) {
            try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
        }
        Start-Sleep -Seconds 2
    }
    Remove-Item -Force $PidFile -ErrorAction SilentlyContinue
    Write-Success 'All AxonHub.NET processes stopped'
    exit 0
}

Write-Info 'Stopping AxonHub.NET...'
$stopped = $false
if (Stop-ByPid) { $stopped = $true }
if (-not $stopped -and (Stop-ByProcessName)) { $stopped = $true }
if (-not $stopped) {
    if (Find-AxonHubProcesses) {
        Write-Err 'Failed to stop all AxonHub.NET processes'
        exit 1
    }
    Write-Info 'No AxonHub.NET process was running'
}
Remove-Item -Force $PidFile -ErrorAction SilentlyContinue
Write-Success 'AxonHub.NET has been stopped'
