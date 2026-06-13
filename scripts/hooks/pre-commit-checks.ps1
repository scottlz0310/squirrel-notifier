<#
.SYNOPSIS
    Pre-commit checks for code quality and security.
.DESCRIPTION
    This script runs various checks. Security checks (private keys, merge conflicts) run on staged Git blobs,
    while style checks (line endings, trailing whitespace, YAML syntax) run on local workspace files.
#>

$ErrorActionPreference = "Stop"

Write-Host "Running pre-commit quality and security checks..." -ForegroundColor Cyan

# Helper function to validate YAML brackets syntax
function Test-YamlSyntax {
    param(
        [string[]]$Lines,
        [string]$Filename
    )

    $failed = $false
    $lineNum = 0
    $bracketStack = @()

    foreach ($line in $Lines) {
        $lineNum++

        $trimmed = $line.Trim()
        if ($trimmed.StartsWith("#") -or $trimmed -eq "") {
            continue
        }

        # Tabs are not allowed in YAML
        if ($line -match '^\t') {
            Write-Host "ERROR: YAML file '$Filename' contains tabs for indentation at line $lineNum." -ForegroundColor Red
            $failed = $true
            continue
        }

        $inSingleQuote = $false
        $inDoubleQuote = $false
        $chars = $line.ToCharArray()

        for ($i = 0; $i -lt $chars.Length; $i++) {
            $char = $chars[$i]

            # Simple quote escape handling
            if ($char -eq "'" -and -not $inDoubleQuote) {
                if ($i -gt 0 -and $chars[$i-1] -eq "\") { continue }
                $inSingleQuote = -not $inSingleQuote
                continue
            }
            if ($char -eq '"' -and -not $inSingleQuote) {
                if ($i -gt 0 -and $chars[$i-1] -eq "\") { continue }
                $inDoubleQuote = -not $inDoubleQuote
                continue
            }

            if ($inSingleQuote -or $inDoubleQuote) {
                continue
            }

            # Bracket validation
            if ($char -eq '[' -or $char -eq '{') {
                $bracketStack += $char
            }
            elseif ($char -eq ']') {
                if ($bracketStack.Length -gt 0 -and $bracketStack[-1] -eq '[') {
                    $bracketStack = $bracketStack[0..($bracketStack.Length - 2)]
                } else {
                    Write-Host "ERROR: YAML syntax error in '$Filename': Unmatched ']' at line $lineNum." -ForegroundColor Red
                    $failed = $true
                }
            }
            elseif ($char -eq '}') {
                if ($bracketStack.Length -gt 0 -and $bracketStack[-1] -eq '{') {
                    $bracketStack = $bracketStack[0..($bracketStack.Length - 2)]
                } else {
                    Write-Host "ERROR: YAML syntax error in '$Filename': Unmatched '}' at line $lineNum." -ForegroundColor Red
                    $failed = $true
                }
            }
        }
    }

    if ($bracketStack.Length -gt 0) {
        Write-Host "ERROR: YAML syntax error in '$Filename': Unclosed bracket(s) '$($bracketStack -join ', ')' at end of file." -ForegroundColor Red
        $failed = $true
    }

    return -not $failed
}

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
    $gitFilePath = $file.Replace('\', '/')
    $ext = [System.IO.Path]::GetExtension($file).ToLowerInvariant()

    # 1. Fetch staged content size
    $sizeStr = (git cat-file -s ":$gitFilePath" 2>$null)
    if ($LASTEXITCODE -ne 0) {
        continue
    }
    $size = [int64]$sizeStr

    # --- PART 1: Staged Blob Security & Integrity Checks ---
    $tempFile = [System.IO.Path]::GetTempFileName()
    try {
        # Export staged blob to temporary file
        cmd.exe /c "git show :`"$gitFilePath`" > `"$tempFile`""

        # Large files check (max 1000KB)
        if ($size -gt 1000 * 1024) {
            Write-Host "ERROR: Staged content for '$file' is too large ($([Math]::Round($size / 1024, 2)) KB). Max allowed size is 1000 KB." -ForegroundColor Red
            $failed = $true
        }

        if (-not ($binaryExtensions -contains $ext)) {
            $rawBytes = [System.IO.File]::ReadAllBytes($tempFile)
            if ($rawBytes.Length -gt 0) {
                $content = [System.Text.Encoding]::UTF8.GetString($rawBytes)

                # Merge conflict marker check
                if ($content -match '(?m)^(<<<<<<<|=======|>>>>>>>)(?:\s|$)') {
                    Write-Host "ERROR: Merge conflict marker found in staged '$file'." -ForegroundColor Red
                    $failed = $true
                }

                # Private key check
                if ($content -match '-----BEGIN[ A-Z0-9_-]+PRIVATE KEY-----') {
                    Write-Host "ERROR: Potential private key detected in staged '$file'!" -ForegroundColor Red
                    $failed = $true
                }
            }
        }
    } finally {
        if (Test-Path $tempFile) {
            Remove-Item $tempFile -Force
        }
    }

    # --- PART 2: Local Workspace File Style & Quality Checks ---
    # Only run style checks if the file physically exists in the workspace
    if (Test-Path $file) {
        if ($binaryExtensions -contains $ext) { continue }

        $rawBytes = [System.IO.File]::ReadAllBytes((Resolve-Path $file).Path)
        if ($rawBytes.Length -eq 0) { continue }

        # Line endings check (CRLF unified)
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

        # End of file newline check
        if ($ext -ne ".svg") {
            $lastByte = $rawBytes[$rawBytes.Length - 1]
            if ($lastByte -ne 10) {
                Write-Host "ERROR: File '$file' does not end with a newline." -ForegroundColor Red
                $failed = $true
            }
        }

        # Trailing whitespace check (exclude markdown files)
        $lines = [System.IO.File]::ReadAllLines((Resolve-Path $file).Path, [System.Text.Encoding]::UTF8)
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

        # YAML syntax validation
        if ($ext -eq ".yml" -or $ext -eq ".yaml") {
            if (-not (Test-YamlSyntax -Lines $lines -Filename $file)) {
                $failed = $true
            }
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
