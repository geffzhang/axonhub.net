param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ArgsFromCmd
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Write-Info([string]$m) { Write-Host "[INFO] $m" -ForegroundColor Cyan }
function Write-Success([string]$m) { Write-Host "[SUCCESS] $m" -ForegroundColor Green }
function Write-Warn([string]$m) { Write-Host "[WARNING] $m" -ForegroundColor Yellow }
function Write-Err([string]$m) { Write-Host "[ERROR] $m" -ForegroundColor Red }

function Show-Usage {
    Write-Host @'
AxonHub.NET installer (Windows)

Usage:
  install.bat [options]

Options:
  --source DIR     Source checkout (default: repo containing this script)
  --db-dsn DSN     PostgreSQL connection string for a new config.yml
  --port PORT      HTTP port for a new config.yml (default: 8090)
  -h, --help       Show this help and exit

The installer publishes a self-contained single-file executable from source.
An existing config.yml is never overwritten. PostgreSQL 17 is required.
'@
}

$SourceDir = $env:AXONHUB_SOURCE_DIR
if (-not $SourceDir) {
    $SourceDir = Split-Path -Parent $PSScriptRoot
}
$BaseDir = Join-Path $env:LOCALAPPDATA 'AxonHub'
$TargetBinary = Join-Path $BaseDir 'axonhub.exe'
$ConfigFile = Join-Path $BaseDir 'config.yml'
$PublishDir = Join-Path $BaseDir '.publish'
$Port = 8090
$Dsn = $env:AXONHUB_DB_DSN
if (-not $Dsn) {
    $Dsn = 'Host=localhost;Port=5432;Database=axonhub;Username=axonhub;Password=axonhub'
}

for ($i = 0; $i -lt $ArgsFromCmd.Count; $i++) {
    $arg = $ArgsFromCmd[$i]
    if ($arg -eq '--source') {
        if ($i + 1 -ge $ArgsFromCmd.Count) { Write-Err '--source requires a directory'; exit 1 }
        $SourceDir = $ArgsFromCmd[++$i]
    } elseif ($arg -eq '--db-dsn') {
        if ($i + 1 -ge $ArgsFromCmd.Count) { Write-Err '--db-dsn requires a value'; exit 1 }
        $Dsn = $ArgsFromCmd[++$i]
    } elseif ($arg -eq '--port') {
        if ($i + 1 -ge $ArgsFromCmd.Count) { Write-Err '--port requires a value'; exit 1 }
        $Port = $ArgsFromCmd[++$i]
    } elseif ($arg -eq '--help' -or $arg -eq '-h') {
        Show-Usage
        exit 0
    } elseif ($arg -match '^--') {
        Write-Err "Unknown option: $arg"
        exit 1
    }
}

if (-not $SourceDir -or -not (Test-Path (Join-Path $SourceDir 'src\AxonHub.Api\AxonHub.Api.csproj'))) {
    Write-Err "Not an axonhub.net source checkout: $SourceDir"
    exit 1
}
$SourceDir = (Resolve-Path $SourceDir).Path

if (-not ($Port -match '^[0-9]+$') -or [int]$Port -lt 1 -or [int]$Port -gt 65535) {
    Write-Err "Invalid port: $Port"
    exit 1
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Err '.NET SDK 10 is required: https://dotnet.microsoft.com/'
    exit 1
}

function Get-Rid {
    switch ($env:PROCESSOR_ARCHITECTURE.ToLowerInvariant()) {
        'amd64' { return 'win-x64' }
        'arm64' { return 'win-arm64' }
        default { Write-Err "Unsupported architecture: $env:PROCESSOR_ARCHITECTURE"; exit 1 }
    }
}

function Ensure-Dirs([string]$path) {
    if (-not (Test-Path $path)) { New-Item -ItemType Directory -Force -Path $path | Out-Null }
}

function Build-Frontend {
    $dist = Join-Path $SourceDir 'frontend\dist'
    if (Test-Path (Join-Path $dist 'index.html')) { return $dist }

    Write-Warn 'frontend/dist is missing; building the admin UI...'
    if (-not (Get-Command pnpm -ErrorAction SilentlyContinue)) {
        Write-Err 'frontend/dist is missing and pnpm is not installed.'
        Write-Info 'Install Node.js 24 + pnpm 10, then run: cd frontend; pnpm install --frozen-lockfile; pnpm build'
        exit 1
    }

    Push-Location (Join-Path $SourceDir 'frontend')
    try {
        & pnpm install --frozen-lockfile | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'pnpm install failed' }
        & pnpm build | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'pnpm build failed' }
    } finally {
        Pop-Location
    }
    return $dist
}

function ConvertTo-YamlValue([string]$value) {
    return $value.Replace('\', '\\').Replace('"', '\"')
}

function Write-Config {
    if (Test-Path $ConfigFile) {
        Write-Info "Keeping existing configuration at $ConfigFile"
        return
    }

    Write-Info "Creating default configuration at $ConfigFile"
    $baseForYaml = ConvertTo-YamlValue ($BaseDir -replace '\\', '/')
    $dsnForYaml = ConvertTo-YamlValue $Dsn
    $lines = @(
        'server:',
        '  host: "0.0.0.0"',
        "  port: $Port",
        '  name: "AxonHub"',
        '  debug: false',
        "  pid_file: `"$baseForYaml/axonhub.pid`"",
        '',
        '# axonhub.net supports PostgreSQL only. PostgreSQL 17 is recommended.',
        'db:',
        '  dialect: postgres',
        "  dsn: `"$dsnForYaml`"",
        '',
        '# Optional multi-silo mode. Keep disabled for one process.',
        'orleans_cluster:',
        '  enabled: false',
        '  provider: adonet',
        '  connection_string: ""',
        '  cluster_id: "axonhub"',
        '  service_id: "axonhub"',
        '  silo_name: ""',
        '  advertised_ip: ""',
        '  silo_port: 11111',
        '  gateway_port: 30000'
    )
    [IO.File]::WriteAllLines($ConfigFile, $lines, (New-Object Text.UTF8Encoding($false)))
    Write-Warn "Edit $ConfigFile and set db.dsn before starting AxonHub.NET."
}

Write-Info 'Preparing AxonHub.NET installation...'
Ensure-Dirs $BaseDir
Ensure-Dirs (Join-Path $BaseDir 'frontend')

$rid = Get-Rid
Write-Info "Platform: $rid"
Write-Info 'Publishing AxonHub.Api (self-contained single file)...'
Remove-Item -Recurse -Force $PublishDir -ErrorAction SilentlyContinue
Ensure-Dirs $PublishDir

& dotnet publish `
    (Join-Path $SourceDir 'src\AxonHub.Api\AxonHub.Api.csproj') `
    --configuration Release `
    --runtime $rid `
    --self-contained true `
    --output $PublishDir `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    Write-Err 'dotnet publish failed'
    exit 1
}

$publishedExe = Join-Path $PublishDir 'AxonHub.Api.exe'
if (-not (Test-Path $publishedExe)) {
    Write-Err "Publish succeeded but $publishedExe was not produced"
    exit 1
}

$dist = Build-Frontend
Write-Info "Installing binary to $TargetBinary"
Copy-Item -LiteralPath $publishedExe -Destination $TargetBinary -Force

$targetDist = Join-Path $BaseDir 'frontend\dist'
if (Test-Path $targetDist) { Remove-Item -Recurse -Force $targetDist }
Copy-Item -LiteralPath $dist -Destination $targetDist -Recurse -Force
Remove-Item -Recurse -Force $PublishDir -ErrorAction SilentlyContinue

Write-Config

$configuredPort = $Port
try {
    Push-Location $BaseDir
    try {
        $value = & $TargetBinary config get server.port 2>$null
        if ($value -match '^[0-9]+$') { $configuredPort = [int]$value }
    } finally {
        Pop-Location
    }
} catch { }

Write-Success 'AxonHub.NET installation completed'
Write-Info "Configuration: $ConfigFile"
Write-Info 'Next steps:'
Write-Host "  1. Edit $ConfigFile and set db.dsn"
Write-Host "  2. Start:      $(Join-Path $PSScriptRoot 'start.bat')"
Write-Host "  3. Stop:       $(Join-Path $PSScriptRoot 'stop.bat')"
Write-Host "  4. Logs:       $BaseDir\axonhub.log"
Write-Host "  5. Web UI:     http://localhost:$configuredPort"
Write-Host "  6. Autostart:  $(Join-Path $PSScriptRoot 'setup.bat') install-autostart"
