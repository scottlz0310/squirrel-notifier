<#
.SYNOPSIS
    Pre-commit checks for code quality and security.
.DESCRIPTION
    This script runs various checks. Security and syntax checks (private keys, merge conflicts, YAML parser)
    run on staged Git blobs, while style checks (line endings, trailing whitespace) run on local workspace files.
#>

$ErrorActionPreference = "Stop"

Write-Host "Running pre-commit quality and security checks..." -ForegroundColor Cyan

# Helper function to validate YAML syntax (brackets, quotes, and indentation)
function Test-YamlSyntax {
    param(
        [string[]]$Lines,
        [string]$Filename
    )

    $failed = $false
    $lineNum = 0

    # Use .NET List for reliable stack operations (avoiding PowerShell array slice bugs)
    $bracketStack = [System.Collections.Generic.List[char]]::new()
    $inSingleQuote = $false
    $inDoubleQuote = $false

    # For duplicate key checking
    $indentStack = [System.Collections.Generic.List[int]]::new()
    $indentStack.Add(-1)
    $keysPerIndent = @{}

    foreach ($line in $Lines) {
        $lineNum++

        $trimmed = $line.Trim()
        if ($trimmed.StartsWith("#") -or $trimmed -eq "") {
            continue
        }

        # 1. Tabs are not allowed in YAML
        if ($line -match '^\t') {
            Write-Host "ERROR: YAML file '$Filename' contains tabs for indentation at line $lineNum." -ForegroundColor Red
            $failed = $true
            continue
        }

        # Calculate indent level
        $line -match '^ *' | Out-Null
        $indent = $Matches[0].Length

        # 2. Check for duplicate keys in same indent level
        $isKeyLine = $false
        $keyName = $null

        if ($line -match '^( *)([^:#''"\{\[ ]+ *):(?: |$)') {
            $isKeyLine = $true
            $keyName = $Matches[2].Trim()
        } elseif ($line -match '^( *)([''"])(.+?)\2 *:(?: |$)') {
            $isKeyLine = $true
            $keyName = $Matches[3]
        }

        if ($isKeyLine) {
            # Maintain indent stack
            while ($indentStack.Count -gt 0 -and $indentStack[$indentStack.Count - 1] -gt $indent) {
                $deepIndent = $indentStack[$indentStack.Count - 1]
                $keysPerIndent.Remove($deepIndent)
                $indentStack.RemoveAt($indentStack.Count - 1)
            }

            if ($indentStack.Count -eq 0 -or $indentStack[$indentStack.Count - 1] -lt $indent) {
                $indentStack.Add($indent)
            }

            if (-not $keysPerIndent.ContainsKey($indent)) {
                $keysPerIndent[$indent] = [System.Collections.Generic.HashSet[string]]::new()
            }

            if ($keysPerIndent[$indent].Contains($keyName)) {
                Write-Host "ERROR: YAML syntax error in staged '$Filename': Duplicate key '$keyName' at line $lineNum." -ForegroundColor Red
                $failed = $true
            } else {
                $keysPerIndent[$indent].Add($keyName) | Out-Null
            }
        }

        # 3. Parse brackets and quotes
        $chars = $line.ToCharArray()
        for ($i = 0; $i -lt $chars.Length; $i++) {
            $char = $chars[$i]

            if ($char -eq "'") {
                if ($inDoubleQuote) { continue }
                if ($i -gt 0 -and $chars[$i-1] -eq "\") { continue }
                $inSingleQuote = -not $inSingleQuote
                continue
            }

            if ($char -eq '"') {
                if ($inSingleQuote) { continue }
                if ($i -gt 0 -and $chars[$i-1] -eq "\") { continue }
                $inDoubleQuote = -not $inDoubleQuote
                continue
            }

            if ($inSingleQuote -or $inDoubleQuote) {
                continue
            }

            if ($char -eq '#') {
                break # Comment line starts
            }

            # Bracket stack operations
            if ($char -eq '[' -or $char -eq '{') {
                $bracketStack.Add($char)
            }
            elseif ($char -eq ']') {
                if ($bracketStack.Count -gt 0 -and $bracketStack[$bracketStack.Count - 1] -eq '[') {
                    $bracketStack.RemoveAt($bracketStack.Count - 1)
                } else {
                    Write-Host "ERROR: YAML syntax error in staged '$Filename': Unmatched ']' at line $lineNum." -ForegroundColor Red
                    $failed = $true
                }
            }
            elseif ($char -eq '}') {
                if ($bracketStack.Count -gt 0 -and $bracketStack[$bracketStack.Count - 1] -eq '{') {
                    $bracketStack.RemoveAt($bracketStack.Count - 1)
                } else {
                    Write-Host "ERROR: YAML syntax error in staged '$Filename': Unmatched '}' at line $lineNum." -ForegroundColor Red
                    $failed = $true
                }
            }
        }

        # 4. Check for unterminated quotes at line end (exclude multiline syntax)
        if ($inSingleQuote -or $inDoubleQuote) {
            $isMultiLine = $trimmed -match ':\s*[\|>]\s*$'
            if (-not $isMultiLine) {
                Write-Host "ERROR: YAML syntax error in staged '$Filename': Unterminated string quote at line $lineNum." -ForegroundColor Red
                $failed = $true
                $inSingleQuote = $false
                $inDoubleQuote = $false
            }
        }
    }

    # 5. Check for unclosed brackets at end of file
    if ($bracketStack.Count -gt 0) {
        Write-Host "ERROR: YAML syntax error in staged '$Filename': Unclosed bracket(s) '$($bracketStack -join ', ')' at end of file." -ForegroundColor Red
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

    # Fetch staged content size
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

                # YAML syntax validation (Checking the actual STAGED blob)
                if ($ext -eq ".yml" -or $ext -eq ".yaml") {
                    $stagedLines = [System.IO.File]::ReadAllLines($tempFile, [System.Text.Encoding]::UTF8)
                    if (-not (Test-YamlSyntax -Lines $stagedLines -Filename $file)) {
                        $failed = $true
                    }
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
    }
}

if ($failed) {
    Write-Host ""
    Write-Host "Pre-commit checks failed! Please resolve the errors above." -ForegroundColor Red
    exit 1
}

Write-Host "✓ All pre-commit quality and security checks passed" -ForegroundColor Green
exit 0
