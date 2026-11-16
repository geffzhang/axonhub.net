param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ArgsFromCmd
)

$ErrorActionPreference = 'Stop'

function Write-Info([string]$m) { Write-Host "[INFO] $m" -ForegroundColor Cyan }
function Write-Success([string]$m) { Write-Host "[SUCCESS] $m" -ForegroundColor Green }
function Write-Err([string]$m) { Write-Host "[ERROR] $m" -ForegroundColor Red }

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$Force = $false

foreach ($arg in $ArgsFromCmd) {
    switch -Regex ($arg) {
        '^(--force)$' { $Force = $true; continue }
        '^(--help|-h)$' {
            Write-Host @'
Usage: restart.bat [--force]

Restarts AxonHub.NET.
Options:
  --force     Force kill before restart
'@
            exit 0
        }
        default { Write-Host "[WARNING] Unknown option: $arg" -ForegroundColor Yellow }
    }
}

Write-Info 'Restarting AxonHub.NET...'
if ($Force) {
    & (Join-Path $ScriptDir 'stop.ps1') --force
} else {
    & (Join-Path $ScriptDir 'stop.ps1')
}

Start-Sleep -Seconds 3
& (Join-Path $ScriptDir 'start.ps1')
if (-not $?) {
    Write-Err 'AxonHub.NET failed to restart'
    exit 1
}
Write-Success 'AxonHub.NET restarted'
