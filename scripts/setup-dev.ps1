<#
.SYNOPSIS
    Setup development environment for Squirrel Notifier.

.DESCRIPTION
    This script sets up the development environment by installing pre-commit
    and configuring Git hooks.

.PARAMETER SkipPreCommit
    Skip pre-commit installation (useful if you want to set up manually).

.EXAMPLE
    .\setup-dev.ps1
    Sets up the development environment.

.EXAMPLE
    .\setup-dev.ps1 -SkipPreCommit
    Sets up the environment without pre-commit.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory=$false)]
    [switch]$SkipPreCommit
)

$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Squirrel Notifier Development Setup" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Check if Python is installed
$pythonInstalled = $null -ne (Get-Command python -ErrorAction SilentlyContinue)

if (-not $pythonInstalled) {
    Write-Host "WARNING: Python is not installed." -ForegroundColor Yellow
    Write-Host "Python is required for pre-commit hooks." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Please install Python from: https://www.python.org/downloads/" -ForegroundColor Cyan
    Write-Host "Or use: winget install Python.Python.3.12" -ForegroundColor Cyan
    Write-Host ""
    $response = Read-Host "Continue without pre-commit setup? (Y/N)"
    if ($response -ne 'Y' -and $response -ne 'y') {
        exit 0
    }
    $SkipPreCommit = $true
}

# Install pre-commit
if (-not $SkipPreCommit) {
    Write-Host "Installing pre-commit..." -ForegroundColor Yellow

    try {
        # Check if pre-commit is already installed
        $preCommitInstalled = $null -ne (Get-Command pre-commit -ErrorAction SilentlyContinue)

        if (-not $preCommitInstalled) {
            python -m pip install --user pre-commit
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to install pre-commit"
            }
            Write-Host "✓ pre-commit installed" -ForegroundColor Green
        } else {
            Write-Host "✓ pre-commit already installed" -ForegroundColor Green
        }

        # Install pre-commit hooks
        Write-Host "Installing pre-commit hooks..." -ForegroundColor Yellow
        pre-commit install
        pre-commit install --hook-type pre-push

        if ($LASTEXITCODE -eq 0) {
            Write-Host "✓ pre-commit hooks installed" -ForegroundColor Green
        } else {
            throw "Failed to install pre-commit hooks"
        }

    } catch {
        Write-Host "ERROR: Failed to setup pre-commit" -ForegroundColor Red
        Write-Host $_.Exception.Message -ForegroundColor Red
        Write-Host ""
        Write-Host "You can setup pre-commit manually later by running:" -ForegroundColor Yellow
        Write-Host "  python -m pip install --user pre-commit" -ForegroundColor White
        Write-Host "  pre-commit install" -ForegroundColor White
        Write-Host "  pre-commit install --hook-type pre-push" -ForegroundColor White
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

if (-not $SkipPreCommit) {
    Write-Host "Pre-commit hooks are now active:" -ForegroundColor Cyan
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

if (-not $SkipPreCommit) {
    Write-Host "To skip pre-commit hooks temporarily:" -ForegroundColor Cyan
    Write-Host "  git commit --no-verify" -ForegroundColor White
    Write-Host "  git push --no-verify" -ForegroundColor White
    Write-Host ""
}

Write-Host "Happy coding! 🚀" -ForegroundColor Green
Write-Host ""
