<#
.SYNOPSIS
    Pre-commit hook to format C# code.

.DESCRIPTION
    This script runs dotnet format to ensure code style consistency before commit.
#>

$ErrorActionPreference = "Stop"

$solutionPath = "winui3/SquirrelNotifier.WinUI3.sln"

Write-Host "Running dotnet format..." -ForegroundColor Cyan

try {
    # Run dotnet format
    $output = dotnet format $solutionPath --verify-no-changes --verbosity quiet 2>&1

    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "ERROR: Code formatting issues detected!" -ForegroundColor Red
        Write-Host "Please run: dotnet format $solutionPath" -ForegroundColor Yellow
        Write-Host ""
        Write-Host "Then stage the changes and commit again." -ForegroundColor Yellow
        exit 1
    }

    Write-Host "✓ Code formatting check passed" -ForegroundColor Green
    exit 0

} catch {
    Write-Host "ERROR: Failed to run dotnet format" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
