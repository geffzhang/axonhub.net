param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ArgsFromCmd
)

$ErrorActionPreference = 'Stop'

function Write-Info([string]$m) { Write-Host "[INFO] $m" -ForegroundColor Cyan }
function Write-Success([string]$m) { Write-Host "[SUCCESS] $m" -ForegroundColor Green }
function Write-Warn([string]$m) { Write-Host "[WARNING] $m" -ForegroundColor Yellow }
function Write-Err([string]$m) { Write-Host "[ERROR] $m" -ForegroundColor Red }

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$SourceDir = $env:AXONHUB_SOURCE_DIR
if (-not $SourceDir) { $SourceDir = Split-Path -Parent $ScriptDir }
$Pull = $true

for ($i = 0; $i -lt $ArgsFromCmd.Count; $i++) {
    $arg = $ArgsFromCmd[$i]
    if ($arg -eq '--source') {
        if ($i + 1 -ge $ArgsFromCmd.Count) { Write-Err '--source requires a directory'; exit 1 }
        $SourceDir = $ArgsFromCmd[++$i]
    } elseif ($arg -eq '--no-pull') {
        $Pull = $false
    } elseif ($arg -eq '--help' -or $arg -eq '-h') {
        Write-Host @'
AxonHub.NET upgrade

Usage:
  upgrade.bat [options]

Options:
  --source DIR   Source checkout (default: repo containing this script)
  --no-pull      Rebuild the current checkout without git pull
  -h, --help     Show this help and exit

The existing config.yml is preserved.
'@
        exit 0
    } else {
        Write-Err "Unknown option: $arg"
        exit 1
    }
}

if (-not (Test-Path (Join-Path $SourceDir 'src\AxonHub.Api\AxonHub.Api.csproj'))) {
    Write-Err "Not an axonhub.net source checkout: $SourceDir"
    exit 1
}
$SourceDir = (Resolve-Path $SourceDir).Path

if ($Pull) {
    $git = Get-Command git -ErrorAction SilentlyContinue
    if ($git) {
        & git -C $SourceDir rev-parse --is-inside-work-tree 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) {
            $dirty = & git -C $SourceDir status --porcelain
            if ($dirty) {
                Write-Err 'Source checkout has uncommitted changes.'
                Write-Info 'Commit/stash them, or run with --no-pull to rebuild the current tree.'
                exit 1
            }
            Write-Info 'Updating source checkout...'
            & git -C $SourceDir pull --ff-only
            if ($LASTEXITCODE -ne 0) {
                Write-Err 'git pull failed'
                exit 1
            }
        } else {
            Write-Warn 'Not a git checkout; rebuilding the current source tree.'
        }
    } else {
        Write-Warn 'git is not installed; rebuilding the current source tree.'
    }
}

Write-Info 'Rebuilding and reinstalling AxonHub.NET...'
& (Join-Path $ScriptDir 'install.ps1') --source $SourceDir
if (-not $?) {
    Write-Err 'Installation failed'
    exit 1
}

Write-Info 'Restarting AxonHub.NET...'
& (Join-Path $ScriptDir 'restart.ps1')
Write-Success 'Upgrade completed'
