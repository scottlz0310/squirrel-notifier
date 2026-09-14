<#
.SYNOPSIS
    headless E2E の専用 root と、root を引数に持つ残留プロセスを削除・検証します。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$RunRoot,

    [string]$ArtifactsDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ProcessIdsUsingRunRoot {
    param(
        [Parameter(Mandatory)]
        [string]$Root
    )

    @(Get-CimInstance -ClassName Win32_Process | Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -and $_.CommandLine.IndexOf($Root, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 } | Select-Object -ExpandProperty ProcessId)
}

function Stop-ProcessesUsingRunRoot {
    param(
        [Parameter(Mandatory)]
        [string]$Root
    )

    $processIds = @(Get-ProcessIdsUsingRunRoot -Root $Root)
    foreach ($processId in $processIds) {
        $process = Get-Process -Id ([int]$processId) -ErrorAction SilentlyContinue
        if ($null -ne $process) {
            $process.Kill($true)
            $process.WaitForExit(5000) | Out-Null
        }
    }

    return $processIds.Count
}

function Write-CleanupArtifact {
    param(
        [Parameter(Mandatory)]
        [string]$Directory,

        [Parameter(Mandatory)]
        [bool]$Success,

        [Parameter(Mandatory)]
        [int]$StoppedProcessCount,

        [Parameter(Mandatory)]
        [bool]$RootRemoved,

        [Parameter(Mandatory)]
        [int]$ResidualProcessCount,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$Errors
    )

    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    [ordered]@{
        schemaVersion = 1
        phase = 'headless'
        success = $Success
        stoppedProcessCount = $StoppedProcessCount
        rootRemoved = $RootRemoved
        residualProcessCount = $ResidualProcessCount
        errors = $Errors
        completedAt = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Directory 'cleanup.json') -Encoding utf8
}

$runRootFullPath = [System.IO.Path]::GetFullPath($RunRoot)
$marker = [System.IO.Path]::DirectorySeparatorChar + 'squirrel-notifier-e2e' + [System.IO.Path]::DirectorySeparatorChar
if (-not $runRootFullPath.Contains($marker, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'RunRoot は squirrel-notifier-e2e 専用 root 配下でなければなりません。'
}

$errors = [System.Collections.Generic.List[string]]::new()
$stoppedProcessCount = 0
$rootRemoved = $false
$residualProcessCount = 0

try {
    $stoppedProcessCount = Stop-ProcessesUsingRunRoot -Root $runRootFullPath

    if (Test-Path -LiteralPath $runRootFullPath) {
        Remove-Item -LiteralPath $runRootFullPath -Recurse -Force
    }
    $rootRemoved = -not (Test-Path -LiteralPath $runRootFullPath)

    $residualProcessCount = @(Get-ProcessIdsUsingRunRoot -Root $runRootFullPath).Count
    if (-not $rootRemoved) {
        $errors.Add('専用 root が残っています。')
    }
    if ($residualProcessCount -ne 0) {
        $errors.Add('専用 root を参照するプロセスが残っています。')
    }
}
catch {
    $errors.Add($_.Exception.GetType().Name)
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($ArtifactsDirectory)) {
        $artifactParameters = @{
            Directory = [System.IO.Path]::GetFullPath($ArtifactsDirectory)
            Success = ($errors.Count -eq 0)
            StoppedProcessCount = $stoppedProcessCount
            RootRemoved = $rootRemoved
            ResidualProcessCount = $residualProcessCount
            Errors = $errors.ToArray()
        }
        Write-CleanupArtifact @artifactParameters
    }
}

if ($errors.Count -ne 0) {
    Write-Error 'headless E2E cleanup に失敗しました。'
    exit 1
}

Write-Host 'headless E2E cleanup に成功しました。'
exit 0
