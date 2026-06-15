<#
.SYNOPSIS
    Install Squirrel Notifier to start automatically at user logon.

.DESCRIPTION
    This script registers Squirrel Notifier with Windows Task Scheduler to start automatically
    when the user logs in. The application will start minimized to the system tray.

.PARAMETER ExePath
    Path to the Squirrel Notifier executable. If not specified, the script will search for it
    in common locations.

.PARAMETER StartMinimized
    If specified, the application will start minimized to the system tray.

.EXAMPLE
    .\install.ps1
    Installs using auto-detected executable path.

.EXAMPLE
    .\install.ps1 -ExePath "C:\Program Files\SquirrelNotifier\SquirrelNotifier.WinUI3.exe"
    Installs using the specified executable path.

.EXAMPLE
    .\install.ps1 -StartMinimized
    Installs with the application starting minimized to system tray.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory=$false)]
    [string]$ExePath,

    [Parameter(Mandatory=$false)]
    [switch]$StartMinimized
)

$ErrorActionPreference = "Stop"

# Task name
$TaskName = "Squirrel Notifier"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Squirrel Notifier Installation Script" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Function to find the executable
function Find-Executable {
    # Prefer executables shipped alongside this script (installer bundle)
    $localExe = Join-Path $PSScriptRoot "SquirrelNotifier.WinUI3.exe"
    if (Test-Path $localExe) {
        return (Resolve-Path $localExe).Path
    }

    # Dynamic search for build output paths
    $buildPaths = @(
        @{
            Base = "$PSScriptRoot\..\winui3\SquirrelNotifier.WinUI3\bin\x64\Release"
            Priority = 1
        },
        @{
            Base = "$PSScriptRoot\..\winui3\SquirrelNotifier.WinUI3\bin\x64\Debug"
            Priority = 2
        }
    )

    # Search only in publish subdirectories to avoid non-self-contained bin/ output
    foreach ($buildPath in ($buildPaths | Sort-Object Priority)) {
        if (Test-Path $buildPath.Base) {
            $exe = Get-ChildItem -Path $buildPath.Base -Filter "SquirrelNotifier.WinUI3.exe" -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.Directory.Name -eq "publish" } |
                Sort-Object LastWriteTime -Descending |
                Select-Object -First 1

            if ($exe) {
                return $exe.FullName
            }
        }
    }

    # Static paths for common installation locations
    $installPaths = @(
        "$env:ProgramFiles\Squirrel Notifier\SquirrelNotifier.WinUI3.exe",
        "$env:LOCALAPPDATA\Squirrel Notifier\SquirrelNotifier.WinUI3.exe"
    )

    foreach ($path in $installPaths) {
        if (Test-Path $path) {
            return (Resolve-Path $path).Path
        }
    }

    return $null
}

# Determine executable path
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    Write-Host "Searching for Squirrel Notifier executable..." -ForegroundColor Yellow
    $ExePath = Find-Executable

    if ($null -eq $ExePath) {
        Write-Host "ERROR: Could not find Squirrel Notifier executable." -ForegroundColor Red
        Write-Host "Please build the project first or specify the path using -ExePath parameter." -ForegroundColor Red
        exit 1
    }

    Write-Host "Found executable: $ExePath" -ForegroundColor Green
} else {
    if (-not (Test-Path $ExePath)) {
        Write-Host "ERROR: Specified executable not found: $ExePath" -ForegroundColor Red
        exit 1
    }
    $ExePath = (Resolve-Path $ExePath).Path
}

Write-Host ""

# Check for old WSL Kernel Watcher task to prompt migration
$oldTaskName = "WSL Kernel Watcher"
$oldTask = Get-ScheduledTask -TaskName $oldTaskName -ErrorAction SilentlyContinue
if ($oldTask) {
    Write-Host "=================================================================" -ForegroundColor Yellow
    Write-Host "警告: 旧バージョンのタスク '$oldTaskName' が登録されています。" -ForegroundColor Yellow
    Write-Host "重複起動を防ぐため、以下の手順で旧バージョンを停止および登録解除してください:" -ForegroundColor Yellow
    Write-Host "  1. 以前のインストールフォルダにある .\uninstall.ps1 を管理者権限で実行する" -ForegroundColor Yellow
    Write-Host "  2. または、タスクスケジューラから手動で '$oldTaskName' を削除する" -ForegroundColor Yellow
    Write-Host "=================================================================" -ForegroundColor Yellow
    Write-Host ""
}

# Check if task already exists
$existingTask = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue

if ($existingTask) {
    Write-Host "WARNING: Task '$TaskName' already exists." -ForegroundColor Yellow
    $response = Read-Host "Do you want to overwrite it? (Y/N)"

    if ($response -ne 'Y' -and $response -ne 'y') {
        Write-Host "Installation cancelled." -ForegroundColor Yellow
        exit 0
    }

    Write-Host "Removing existing task..." -ForegroundColor Yellow
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

# Prepare arguments
$arguments = if ($StartMinimized) { "--tray" } else { $null }

# Create task action (引数が空の場合は -Argument を指定しない)
if ([string]::IsNullOrWhiteSpace($arguments)) {
    $action = New-ScheduledTaskAction -Execute $ExePath
} else {
    $action = New-ScheduledTaskAction -Execute $ExePath -Argument $arguments
}

# Create task trigger (at logon)
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME

# Create task settings
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RunOnlyIfNetworkAvailable:$false `
    -DontStopOnIdleEnd `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1)

# Create task principal (run as current user)
$principal = New-ScheduledTaskPrincipal `
    -UserId $env:USERNAME `
    -LogonType Interactive `
    -RunLevel Limited

# Register the task
try {
    Write-Host "Registering scheduled task..." -ForegroundColor Yellow

    $task = Register-ScheduledTask `
        -TaskName $TaskName `
        -Action $action `
        -Trigger $trigger `
        -Settings $settings `
        -Principal $principal `
        -Description "Monitors external MCP resources and notifies when review updates are available."

    Write-Host ""
    Write-Host "SUCCESS: Task registered successfully!" -ForegroundColor Green
    Write-Host ""
    Write-Host "Task Details:" -ForegroundColor Cyan
    Write-Host "  Name: $TaskName"
    Write-Host "  Executable: $ExePath"
    Write-Host "  Arguments: $(if ($arguments) { $arguments } else { '(none)' })"
    Write-Host "  Trigger: At user logon ($env:USERNAME)"
    Write-Host ""

    # Ask if user wants to start the task now
    $response = Read-Host "Do you want to start Squirrel Notifier now? (Y/N)"

    if ($response -eq 'Y' -or $response -eq 'y') {
        Write-Host "Starting task..." -ForegroundColor Yellow
        Start-ScheduledTask -TaskName $TaskName
        Start-Sleep -Seconds 2

        # Check if the process is running
        $processName = [System.IO.Path]::GetFileNameWithoutExtension($ExePath)
        $process = Get-Process -Name $processName -ErrorAction SilentlyContinue

        if ($process) {
            Write-Host "SUCCESS: Squirrel Notifier is now running!" -ForegroundColor Green
            if ($StartMinimized) {
                Write-Host "The application is running in the system tray." -ForegroundColor Cyan
            }
        } else {
            Write-Host "WARNING: Task started but process not detected. Please check Task Scheduler." -ForegroundColor Yellow
        }
    }

    Write-Host ""
    Write-Host "Installation complete! Squirrel Notifier will start automatically at logon." -ForegroundColor Green
    Write-Host ""
    Write-Host "To uninstall, run: .\uninstall.ps1" -ForegroundColor Cyan

} catch {
    Write-Host "ERROR: Failed to register task." -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
