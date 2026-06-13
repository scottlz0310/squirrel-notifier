<#
.SYNOPSIS
    Pre-commit checks for code quality and security.
.DESCRIPTION
    This script runs various checks on staged files (e.g. trailing whitespace, large files, merge conflicts, private keys).
#>

$ErrorActionPreference = "Stop"

Write-Host "Running pre-commit quality and security checks..." -ForegroundColor Cyan

# Get staged files
$stagedFiles = git diff --cached --name-only --diff-filter=ACM
if ($null -eq $stagedFiles -or $stagedFiles.Count -eq 0) {
    Write-Host "No staged files to check." -ForegroundColor Green
    exit 0
}

$binaryExtensions = @(".png", ".jpg", ".jpeg", ".gif", ".ico", ".zip", ".pdf", ".dll", ".exe")
$failed = $false

# A. Case conflict check
$lowercasePaths = @{}
foreach ($file in $stagedFiles) {
    $lower = $file.ToLowerInvariant()
    if ($lowercasePaths.ContainsKey($lower)) {
        Write-Host "ERROR: Case conflict detected between '$file' and '$($lowercasePaths[$lower])'" -ForegroundColor Red
        $failed = $true
    }
    $lowercasePaths[$lower] = $file
}

# B. Checks per file
foreach ($file in $stagedFiles) {
    if (-not (Test-Path $file)) { continue }

    # 1. Large files check (max 1000KB)
    $size = (Get-Item $file).Length
    if ($size -gt 1000 * 1024) {
        Write-Host "ERROR: File '$file' is too large ($([Math]::Round($size / 1024, 2)) KB). Max allowed size is 1000 KB." -ForegroundColor Red
        $failed = $true
    }

    # Skip binary files for content checks
    $ext = [System.IO.Path]::GetExtension($file).ToLowerInvariant()
    if ($binaryExtensions -contains $ext) { continue }

    # Content-based checks
    $rawBytes = [System.IO.File]::ReadAllBytes((Resolve-Path $file).Path)
    if ($rawBytes.Length -eq 0) { continue }

    # End of file newline check (end-of-file-fixer)
    if ($ext -ne ".svg") {
        $lastByte = $rawBytes[$rawBytes.Length - 1]
        if ($lastByte -ne 10) { # 10 is LF (\n)
            Write-Host "ERROR: File '$file' does not end with a newline." -ForegroundColor Red
            $failed = $true
        }
    }

    # Read as text for text checks (using UTF8)
    $lines = Get-Content -Path $file -Encoding UTF8 -ErrorAction SilentlyContinue
    if ($null -eq $lines) { continue }

    # Trailing whitespace check (exclude markdown files)
    if ($ext -ne ".md") {
        $lineNum = 1
        foreach ($line in $lines) {
            if ($line -match '\s+$') {
                Write-Host "ERROR: Trailing whitespace found in '$file' at line $lineNum." -ForegroundColor Red
                $failed = $true
                break
            }
            $lineNum++
        }
    }

    # Re-read raw content as single string
    $content = [System.Text.Encoding]::UTF8.GetString($rawBytes)

    # Merge conflict marker check
    if ($content -match '(?m)^(<<<<<<<|=======|>>>>>>>)(?:\s|$)') {
        Write-Host "ERROR: Merge conflict marker found in '$file'." -ForegroundColor Red
        $failed = $true
    }

    # Private key check
    if ($content -match '-----BEGIN[ A-Z0-9_-]+PRIVATE KEY-----') {
        Write-Host "ERROR: Potential private key detected in '$file'!" -ForegroundColor Red
        $failed = $true
    }

    # Mixed line ending check (ensure only CRLF, no bare LFs)
    $hasLFWithoutCR = $false
    for ($i = 0; $i -lt $rawBytes.Length; $i++) {
        if ($rawBytes[$i] -eq 10) { # LF (\n)
            if ($i -eq 0 -or $rawBytes[$i-1] -ne 13) { # Not CR (\r)
                $hasLFWithoutCR = $true
                break
            }
        }
    }
    if ($hasLFWithoutCR) {
        Write-Host "ERROR: Non-CRLF (LF-only) line endings detected in '$file'. Only CRLF is allowed." -ForegroundColor Red
        $failed = $true
    }

    # YAML checks (check-yaml)
    if ($ext -eq ".yml" -or $ext -eq ".yaml") {
        $lineNum = 1
        foreach ($line in $lines) {
            if ($line -match '^\t') {
                Write-Host "ERROR: YAML file '$file' contains tabs for indentation at line $lineNum." -ForegroundColor Red
                $failed = $true
                break
            }
            $lineNum++
        }
    }
}

if ($failed) {
    Write-Host ""
    Write-Host "Pre-commit checks failed! Please resolve the errors above." -ForegroundColor Red
    exit 1
}

Write-Host "✓ All pre-commit quality and security checks passed" -ForegroundColor Green
exit 0
