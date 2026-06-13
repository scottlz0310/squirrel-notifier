<#
.SYNOPSIS
    Uninstall Squirrel Notifier from Task Scheduler.

.DESCRIPTION
    This script removes the Squirrel Notifier scheduled task and optionally stops
    the running process.

.PARAMETER KeepSettings
    If specified, user settings will be preserved. Otherwise, settings will be deleted.

.EXAMPLE
    .\uninstall.ps1
    Uninstalls the task and removes all settings.

.EXAMPLE
    .\uninstall.ps1 -KeepSettings
    Uninstalls the task but keeps user settings.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory=$false)]
    [switch]$KeepSettings
)

$ErrorActionPreference = "Stop"

# Task name
$TaskName = "Squirrel Notifier"

Write-Host "===========================================" -ForegroundColor Cyan
Write-Host "Squirrel Notifier Uninstallation Script" -ForegroundColor Cyan
Write-Host "===========================================" -ForegroundColor Cyan
Write-Host ""

# Check if task exists
$existingTask = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue

if (-not $existingTask) {
    Write-Host "WARNING: Task '$TaskName' not found in Task Scheduler." -ForegroundColor Yellow
    Write-Host "It may have already been uninstalled." -ForegroundColor Yellow
} else {
    # Check if process is running
    $processName = "SquirrelNotifier.WinUI3"
    $process = Get-Process -Name $processName -ErrorAction SilentlyContinue

    if ($process) {
        Write-Host "Squirrel Notifier is currently running." -ForegroundColor Yellow
        $response = Read-Host "Do you want to stop it? (Y/N)"

        if ($response -eq 'Y' -or $response -eq 'y') {
            Write-Host "Stopping Squirrel Notifier..." -ForegroundColor Yellow
            try {
                Stop-Process -Name $processName -Force -ErrorAction Stop
                Start-Sleep -Seconds 2
                Write-Host "Process stopped successfully." -ForegroundColor Green
            } catch {
                Write-Host "WARNING: Failed to stop process. You may need to close it manually." -ForegroundColor Yellow
            }
        }
    }

    # Remove the scheduled task
    Write-Host "Removing scheduled task..." -ForegroundColor Yellow
    try {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-Host "SUCCESS: Task removed successfully!" -ForegroundColor Green
    } catch {
        Write-Host "ERROR: Failed to remove task." -ForegroundColor Red
        Write-Host $_.Exception.Message -ForegroundColor Red
        exit 1
    }
}

Write-Host ""

# Handle settings
$settingsPath = "$env:LOCALAPPDATA\SquirrelNotifier"

if (Test-Path $settingsPath) {
    if ($KeepSettings) {
        Write-Host "User settings preserved at: $settingsPath" -ForegroundColor Cyan
    } else {
        $response = Read-Host "Do you want to delete user settings? (Y/N)"

        if ($response -eq 'Y' -or $response -eq 'y') {
            Write-Host "Deleting user settings..." -ForegroundColor Yellow
            try {
                Remove-Item -Path $settingsPath -Recurse -Force
                Write-Host "Settings deleted successfully." -ForegroundColor Green
            } catch {
                Write-Host "WARNING: Failed to delete settings." -ForegroundColor Yellow
                Write-Host $_.Exception.Message -ForegroundColor Yellow
            }
        } else {
            Write-Host "User settings preserved at: $settingsPath" -ForegroundColor Cyan
        }
    }
}

Write-Host ""
Write-Host "Uninstallation complete!" -ForegroundColor Green
Write-Host ""
Write-Host "To reinstall, run: .\install.ps1" -ForegroundColor Cyan
Write-Host ""
