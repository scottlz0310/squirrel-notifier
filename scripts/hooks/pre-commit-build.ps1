<#
.SYNOPSIS
    Pre-commit hook to build the solution.

.DESCRIPTION
    This script builds the solution with Release configuration to catch build errors
    before commit. Warnings are treated as errors.
#>

$ErrorActionPreference = "Stop"

$solutionPath = "winui3/SquirrelNotifier.WinUI3.sln"

Write-Host "Building solution..." -ForegroundColor Cyan

try {
    # Build the solution
    $output = dotnet build $solutionPath `
        --configuration Release `
        --nologo `
        --verbosity quiet `
        /p:TreatWarningsAsErrors=true `
        /p:Platform=x64 2>&1

    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "ERROR: Build failed!" -ForegroundColor Red
        Write-Host ""
        Write-Host "Output:" -ForegroundColor Yellow
        Write-Host $output
        Write-Host ""
        Write-Host "Please fix the build errors before committing." -ForegroundColor Yellow
        exit 1
    }

    Write-Host "✓ Build passed" -ForegroundColor Green
    exit 0

} catch {
    Write-Host "ERROR: Failed to build solution" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
