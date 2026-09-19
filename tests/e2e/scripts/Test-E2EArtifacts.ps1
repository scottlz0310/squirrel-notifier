<#
.SYNOPSIS
    headless E2E artifact の必須ファイルと session ID / secret pattern 非露出を検査します。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ArtifactsDirectory,

    [switch]$ScanOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$directory = [System.IO.Path]::GetFullPath($ArtifactsDirectory)
if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
    throw 'E2E artifact directory がありません。'
}

$requiredFiles = @(
    'result.json',
    'versions.json',
    'sanitized.log',
    'cleanup.json'
)
$missingFiles = [System.Collections.Generic.List[string]]::new()
if (-not $ScanOnly) {
    foreach ($requiredFile in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $directory $requiredFile) -PathType Leaf)) {
            $missingFiles.Add($requiredFile)
        }
    }
}

$guidPattern = '(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b'
$secretPatterns = @(
    '(?i)\bgh[pousr]_[A-Za-z0-9_]{20,}\b',
    '(?i)\bgithub_pat_[A-Za-z0-9_]{20,}\b',
    '(?i)\bsk-[A-Za-z0-9]{20,}\b',
    '(?i)\bBearer\s+[A-Za-z0-9._-]{20,}\b'
)
$violations = [System.Collections.Generic.List[string]]::new()
$textExtensions = @('.json', '.log', '.txt', '.md')

foreach ($file in @(Get-ChildItem -LiteralPath $directory -File -Recurse)) {
    if ($textExtensions -notcontains $file.Extension.ToLowerInvariant()) {
        continue
    }

    $content = Get-Content -LiteralPath $file.FullName -Raw -Encoding utf8
    if ($content -match $guidPattern) {
        $violations.Add("$($file.Name): session-id pattern")
    }

    foreach ($pattern in $secretPatterns) {
        if ($content -match $pattern) {
            $violations.Add("$($file.Name): secret pattern")
            break
        }
    }
}

if ($missingFiles.Count -ne 0 -or $violations.Count -ne 0) {
    if ($missingFiles.Count -ne 0) {
        Write-Error "E2E artifact の必須ファイルがありません: $($missingFiles -join ', ')"
    }
    if ($violations.Count -ne 0) {
        Write-Error 'E2E artifact に session ID または secret pattern が含まれています。'
        $violations | ForEach-Object { Write-Error $_ }
    }
    exit 1
}

Write-Host "E2E artifact 検査に成功しました: $directory"
exit 0
