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
$BinaryPath = Join-Path $BaseDir 'axonhub.exe'
$ConfigFile = Join-Path $BaseDir 'config.yml'
$StartupFolder = [Environment]::GetFolderPath('Startup')
$ShortcutPath = Join-Path $StartupFolder 'AxonHub.lnk'
$TaskName = 'AxonHubAutoStart'

function Show-Usage {
    Write-Host @'
AxonHub.NET Setup (Windows)

Usage:
  setup.bat [command]

Commands:
  install-autostart    Start AxonHub.NET automatically at logon
  uninstall-autostart  Remove automatic startup
  status               Show autostart and process status
'@
}

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-AutostartStatus {
    $startup = Test-Path $ShortcutPath
    $task = $null
    try { $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue } catch { }
    return @{
        StartupFolder = $startup
        TaskScheduler = [bool]$task
        TaskEnabled = if ($task) { $task.State -ne 'Disabled' } else { $false }
    }
}

function Install-StartupFolder {
    Write-Info 'Installing startup shortcut...'
    if (-not (Test-Path $BinaryPath)) {
        Write-Err "AxonHub.NET not found at $BinaryPath"
        Write-Info 'Run install.bat first.'
        return $false
    }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $BinaryPath
    $shortcut.WorkingDirectory = $BaseDir
    $shortcut.IconLocation = $BinaryPath
    $shortcut.Save()
    Write-Success "Startup shortcut created: $ShortcutPath"
    return $true
}

function Uninstall-StartupFolder {
    if (Test-Path $ShortcutPath) {
        Remove-Item -Path $ShortcutPath -Force
        Write-Success 'Startup shortcut removed'
        return $true
    }
    Write-Warn 'No startup shortcut found'
    return $false
}

function Install-TaskScheduler {
    Write-Info 'Installing scheduled task for auto-start...'
    if (-not (Test-Path $BinaryPath)) {
        Write-Err "AxonHub.NET not found at $BinaryPath"
        Write-Info 'Run install.bat first.'
        return $false
    }
    if (-not (Test-Admin)) {
        Write-Warn 'Administrator privileges are required for the scheduled task'
        Write-Info 'Elevating...'
        $scriptPath = $MyInvocation.MyCommand.Path
        Start-Process powershell -Verb RunAs -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$scriptPath`"", 'install-autostart'
        )
        return $true
    }

    try { Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue } catch { }
    $action = New-ScheduledTaskAction -Execute $BinaryPath -WorkingDirectory $BaseDir
    $trigger = New-ScheduledTaskTrigger -AtLogOn
    $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable

    try {
        Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
            -Principal $principal -Settings $settings -Force | Out-Null
        Write-Success "Scheduled task '$TaskName' created"
        return $true
    } catch {
        Write-Err "Failed to create scheduled task: $_"
        return $false
    }
}

function Uninstall-TaskScheduler {
    if (-not (Test-Admin)) {
        Write-Warn 'Administrator privileges are required'
        $scriptPath = $MyInvocation.MyCommand.Path
        Start-Process powershell -Verb RunAs -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$scriptPath`"", 'uninstall-autostart'
        )
        return $true
    }
    try {
        $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        if ($task) {
            Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
            Write-Success "Scheduled task '$TaskName' removed"
            return $true
        }
    } catch {
        Write-Warn "Could not remove scheduled task: $_"
    }
    Write-Warn "No scheduled task named '$TaskName'"
    return $false
}

function Show-Status {
    Write-Info 'AxonHub.NET status'
    $status = Get-AutostartStatus
    Write-Host "  Startup Folder: $(if ($status.StartupFolder) { 'ENABLED' } else { 'DISABLED' })"
    Write-Host "  Task Scheduler: $(if ($status.TaskScheduler) { if ($status.TaskEnabled) { 'ENABLED' } else { 'DISABLED (task exists)' } } else { 'DISABLED' })"

    $procs = Get-Process -Name 'axonhub' -ErrorAction SilentlyContinue
    if ($procs) {
        Write-Host ('  Process:        running (' + (($procs | ForEach-Object { $_.Id }) -join ', ') + ')')
    } else {
        Write-Host '  Process:        not running'
    }
    Write-Host "  Base:           $BaseDir"
    Write-Host "  Binary:         $BinaryPath"
    Write-Host "  Config:         $ConfigFile"
}

$Command = $null
foreach ($arg in $ArgsFromCmd) {
    switch -Regex ($arg) {
        '^(--help|-h)$' { Show-Usage; exit 0 }
        '^(install-autostart|enable-autostart)$' { $Command = 'install'; continue }
        '^(uninstall-autostart|disable-autostart)$' { $Command = 'uninstall'; continue }
        '^(status|check)$' { $Command = 'status'; continue }
        default { Write-Warn "Unknown argument: $arg" }
    }
}

switch ($Command) {
    'install' {
        $ok = Install-TaskScheduler
        if (-not $ok) {
            Write-Info 'Falling back to the Startup folder'
            $ok = Install-StartupFolder
        }
        if ($ok) { Write-Success 'Auto-start installation completed' }
    }
    'uninstall' {
        $task = Uninstall-TaskScheduler
        $shortcut = Uninstall-StartupFolder
        if ($task -or $shortcut) { Write-Success 'Auto-start removal completed' }
    }
    'status' { Show-Status }
    default { Show-Usage }
}
