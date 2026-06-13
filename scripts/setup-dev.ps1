<#
.SYNOPSIS
    Setup development environment for Squirrel Notifier.

.DESCRIPTION
    This script sets up the development environment by installing Lefthook Git hooks
    and restoring NuGet packages.

.PARAMETER SkipHooks
    Skip Lefthook installation.

.EXAMPLE
    .\setup-dev.ps1
    Sets up the development environment.

.EXAMPLE
    .\setup-dev.ps1 -SkipHooks
    Sets up the environment without Lefthook hooks.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory=$false)]
    [switch]$SkipHooks
)

$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Squirrel Notifier Development Setup" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Check if Lefthook is installed
if (-not $SkipHooks) {
    $lefthookInstalled = $null -ne (Get-Command lefthook -ErrorAction SilentlyContinue)

    if (-not $lefthookInstalled) {
        Write-Host "ERROR: Lefthook is not installed." -ForegroundColor Red
        Write-Host "Lefthook is required for running Git hooks under our standard quality toolchain." -ForegroundColor Red
        Write-Host ""
        Write-Host "Please install Lefthook using one of the following methods:" -ForegroundColor Cyan
        Write-Host "  • winget: winget install evilmartians.lefthook" -ForegroundColor Cyan
        Write-Host "  • scoop:  scoop install lefthook" -ForegroundColor Cyan
        Write-Host "  • npm:    npm install -g @evilmartians/lefthook" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "Or, if you intentionally want to bypass hooks (not recommended for production development)," -ForegroundColor Yellow
        Write-Host "run the setup script with the -SkipHooks parameter:" -ForegroundColor Yellow
        Write-Host "  pwsh -File .\scripts\setup-dev.ps1 -SkipHooks" -ForegroundColor White
        Write-Host ""
        exit 1
    }
}

# Install Lefthook hooks
if (-not $SkipHooks) {
    Write-Host "Installing Lefthook Git hooks..." -ForegroundColor Yellow

    try {
        lefthook install
        if ($LASTEXITCODE -eq 0) {
            Write-Host "✓ Lefthook hooks installed successfully" -ForegroundColor Green
        } else {
            throw "Failed to install lefthook hooks"
        }
    } catch {
        Write-Host "ERROR: Failed to setup Lefthook" -ForegroundColor Red
        Write-Host $_.Exception.Message -ForegroundColor Red
        Write-Host ""
        Write-Host "You can setup Lefthook manually later by running:" -ForegroundColor Yellow
        Write-Host "  lefthook install" -ForegroundColor White
    }
}

Write-Host ""
Write-Host "Restoring NuGet packages..." -ForegroundColor Yellow

try {
    dotnet restore winui3/SquirrelNotifier.WinUI3.sln
    if ($LASTEXITCODE -eq 0) {
        Write-Host "✓ NuGet packages restored" -ForegroundColor Green
    }
} catch {
    Write-Host "WARNING: Failed to restore NuGet packages" -ForegroundColor Yellow
    Write-Host "You may need to restore manually: dotnet restore" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Development environment setup complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

if (-not $SkipHooks) {
    Write-Host "Lefthook Git hooks are now active:" -ForegroundColor Cyan
    Write-Host "  • On commit: Code formatting & build check" -ForegroundColor White
    Write-Host "  • On push: Test execution & coverage check" -ForegroundColor White
    Write-Host ""
}

Write-Host "Quick commands:" -ForegroundColor Cyan
Write-Host "  • Format code:  dotnet format winui3/SquirrelNotifier.WinUI3.sln" -ForegroundColor White
Write-Host "  • Build:        dotnet build winui3/SquirrelNotifier.WinUI3.sln -c Release /p:Platform=x64" -ForegroundColor White
Write-Host "  • Run tests:    dotnet test winui3/SquirrelNotifier.WinUI3.sln" -ForegroundColor White
Write-Host "  • Install app:  .\scripts\install.ps1 -StartMinimized" -ForegroundColor White
Write-Host ""

if (-not $SkipHooks) {
    Write-Host "To skip Lefthook hooks temporarily:" -ForegroundColor Cyan
    Write-Host "  git commit --no-verify" -ForegroundColor White
    Write-Host "  git push --no-verify" -ForegroundColor White
    Write-Host ""
}

Write-Host "Happy coding! 🚀" -ForegroundColor Green
Write-Host ""
