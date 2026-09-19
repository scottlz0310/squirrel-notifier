<#
.SYNOPSIS
    Phase 1 headless E2E を build、実行、cleanup、artifact 検査まで一貫して行います。
#>
[CmdletBinding()]
param(
    [ValidateSet('Headless')]
    [string]$Phase = 'Headless',

    [string]$Scenario,

    [string]$ArtifactsDirectory = (Join-Path (Get-Location) 'artifacts\e2e-local')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not [System.OperatingSystem]::IsWindows()) {
    throw 'Phase 1 headless E2E は Windows runner でのみ実行できます。'
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$scenarioDirectory = Join-Path $repoRoot 'tests\e2e\scenarios'
$runnerProject = Join-Path $repoRoot 'tests\e2e\SquirrelNotifier.HeadlessE2E\SquirrelNotifier.HeadlessE2E.csproj'
$dummyProject = Join-Path $repoRoot 'tests\e2e\fixtures\launcher\DummyLauncher\DummyLauncher.csproj'
$subscriberProject = Join-Path $repoRoot 'tests\e2e\fixtures\subscriber\DummySubscriber\DummySubscriber.csproj'
$cleanupScript = Join-Path $PSScriptRoot 'Invoke-E2ECleanup.ps1'
$artifactCheckScript = Join-Path $PSScriptRoot 'Test-E2EArtifacts.ps1'
$artifactRunRoot = Join-Path ([System.IO.Path]::GetFullPath($ArtifactsDirectory)) ('headless\' + [Guid]::NewGuid().ToString('N'))

$manifests = @(Get-ChildItem -LiteralPath $scenarioDirectory -Filter '*.json' -File | Sort-Object Name)
if (-not [string]::IsNullOrWhiteSpace($Scenario)) {
    $manifests = @($manifests | Where-Object { $_.BaseName -eq $Scenario })
}
if ($manifests.Count -eq 0) {
    throw '指定された headless E2E scenario manifest が見つかりません。'
}

New-Item -ItemType Directory -Path $artifactRunRoot -Force | Out-Null
$runnerTemp = if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    [System.IO.Path]::GetTempPath()
} else {
    $env:RUNNER_TEMP
}
$runRoot = Join-Path ([System.IO.Path]::GetFullPath($runnerTemp)) ('squirrel-notifier-e2e\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

function Write-ScenarioFailure {
    param(
        [Parameter(Mandatory)]
        [string]$Directory,

        [Parameter(Mandatory)]
        [string]$Category,

        [Parameter(Mandatory)]
        [string]$Message
    )

    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    $now = [DateTimeOffset]::UtcNow.ToString('O')
    [ordered]@{
        schemaVersion = 1
        phase = 'headless'
        scenarioId = (Split-Path -Leaf $Directory)
        category = $Category
        component = 'squirrel-notifier'
        message = $Message
        startedAt = $now
        completedAt = $now
        artifactHints = @('sanitized.log', 'command-lines.json', 'settings-sanitized.json', 'versions.json')
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Directory 'failure.json') -Encoding utf8

    if (-not (Test-Path -LiteralPath (Join-Path $Directory 'result.json') -PathType Leaf)) {
        [ordered]@{
            schemaVersion = 1
            phase = 'headless'
            scenarioId = (Split-Path -Leaf $Directory)
            outcome = 'failed'
            startedAt = $now
            completedAt = $now
            assertions = @()
            invocationCount = 0
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Directory 'result.json') -Encoding utf8
    }
}

Write-Host "headless E2E run root: $runRoot"
Write-Host "headless E2E artifact root: $artifactRunRoot"

try {
    dotnet restore $dummyProject
    if ($LASTEXITCODE -ne 0) {
        throw 'dummy launcher の依存関係復元に失敗しました。'
    }

    dotnet restore $subscriberProject
    if ($LASTEXITCODE -ne 0) {
        throw 'dummy subscriber の依存関係復元に失敗しました。'
    }

    dotnet restore $runnerProject /p:IncludeWindowsSdkBuildTools=false
    if ($LASTEXITCODE -ne 0) {
        throw 'headless E2E runner の依存関係復元に失敗しました。'
    }

    dotnet format $dummyProject --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw 'dummy launcher の format 検証に失敗しました。'
    }

    dotnet format $subscriberProject --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw 'dummy subscriber の format 検証に失敗しました。'
    }

    dotnet format $runnerProject --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw 'headless E2E runner の format 検証に失敗しました。'
    }

    dotnet build $dummyProject --configuration Release --no-restore /p:Platform=x64 /p:TreatWarningsAsErrors=true
    if ($LASTEXITCODE -ne 0) {
        throw 'dummy launcher の build に失敗しました。'
    }

    dotnet build $subscriberProject --configuration Release --no-restore /p:Platform=x64 /p:TreatWarningsAsErrors=true
    if ($LASTEXITCODE -ne 0) {
        throw 'dummy subscriber の build に失敗しました。'
    }

    dotnet build $runnerProject --configuration Release --no-restore /p:Platform=x64 /p:IncludeWindowsSdkBuildTools=false /p:TreatWarningsAsErrors=true
    if ($LASTEXITCODE -ne 0) {
        throw 'headless E2E runner の build に失敗しました。'
    }

    $dummyLauncher = Get-ChildItem -LiteralPath (Join-Path (Split-Path $dummyProject) 'bin') -Recurse -Filter 'codex.exe' -File |
        Where-Object { $_.FullName -like '*\x64\Release\net10.0\win-x64\codex.exe' } |
        Select-Object -First 1
    $subscriberFixture = Get-ChildItem -LiteralPath (Join-Path (Split-Path $subscriberProject) 'bin') -Recurse -Filter 'mcp-resource-subscriber.exe' -File |
        Where-Object { $_.FullName -like '*\x64\Release\net10.0\win-x64\mcp-resource-subscriber.exe' } |
        Select-Object -First 1
    $runner = Get-ChildItem -LiteralPath (Join-Path (Split-Path $runnerProject) 'bin') -Recurse -Filter 'SquirrelNotifier.HeadlessE2E.exe' -File |
        Where-Object { $_.FullName -like '*\x64\Release\net10.0-windows10.0.26100.0\win-x64\SquirrelNotifier.HeadlessE2E.exe' } |
        Select-Object -First 1
    if ($null -eq $dummyLauncher -or $null -eq $subscriberFixture -or $null -eq $runner) {
        throw 'build output から E2E runner、dummy launcher、または dummy subscriber を解決できません。'
    }

    $overallExitCode = 0
    $summaryRows = [System.Collections.Generic.List[string]]::new()
    foreach ($manifest in $manifests) {
        $scenarioRoot = Join-Path $runRoot $manifest.BaseName
        $scenarioArtifacts = Join-Path $artifactRunRoot $manifest.BaseName
        New-Item -ItemType Directory -Path $scenarioRoot, $scenarioArtifacts -Force | Out-Null
        $scenarioExitCode = 1
        $cleanupExitCode = 1
        $artifactCheckExitCode = 1

        try {
            $runnerArguments = @(
                '--scenario-file', $manifest.FullName,
                '--run-root', $scenarioRoot,
                '--artifacts-directory', $scenarioArtifacts,
                '--dummy-launcher', $dummyLauncher.FullName,
                '--subscriber-fixture', $subscriberFixture.FullName
            )
            & $runner.FullName @runnerArguments
            $scenarioExitCode = $LASTEXITCODE
            if ($scenarioExitCode -ne 0) {
                $overallExitCode = 1
            }
        }
        catch {
            Write-ScenarioFailure -Directory $scenarioArtifacts -Category 'TEST_HARNESS_FAILED' -Message 'E2E runner の起動に失敗しました。'
            $overallExitCode = 1
        }
        finally {
            $cleanupArguments = @(
                '-NoProfile',
                '-File', $cleanupScript,
                '-RunRoot', $scenarioRoot,
                '-ArtifactsDirectory', $scenarioArtifacts
            )
            & pwsh @cleanupArguments
            $cleanupExitCode = $LASTEXITCODE
            if ($cleanupExitCode -ne 0) {
                Write-ScenarioFailure -Directory $scenarioArtifacts -Category 'CLEANUP_FAILED' -Message 'E2E scenario の cleanup に失敗しました。'
                $overallExitCode = 1
            }

            & pwsh -NoProfile -File $artifactCheckScript -ArtifactsDirectory $scenarioArtifacts
            $artifactCheckExitCode = $LASTEXITCODE
            if ($artifactCheckExitCode -ne 0) {
                Write-ScenarioFailure -Directory $scenarioArtifacts -Category 'SECURITY_SECRET_EXPOSURE' -Message 'E2E artifact の secret scan または必須ファイル検査に失敗しました。'
                $overallExitCode = 1
            }
        }

        $status = if ($scenarioExitCode -eq 0 -and $cleanupExitCode -eq 0 -and $artifactCheckExitCode -eq 0) { 'passed' } else { 'failed' }
        $summaryRows.Add("| $($manifest.BaseName) | $status | $scenarioExitCode | $cleanupExitCode | $artifactCheckExitCode |")
        if ($cleanupExitCode -ne 0) {
            break
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
        Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value @(
            '## Squirrel Notifier Phase 1 headless E2E'
            ''
            '| scenario | status | runner | cleanup | artifact |'
            '|---|---|---:|---:|---:|'
            $summaryRows
            ''
            "artifact root: $artifactRunRoot"
        )
    }

    exit $overallExitCode
}
finally {
    if (Test-Path -LiteralPath $runRoot) {
        & pwsh -NoProfile -File $cleanupScript -RunRoot $runRoot
        if ($LASTEXITCODE -ne 0) {
            Write-Error 'scenario 共通 root の cleanup に失敗しました。'
            exit 1
        }
    }
}
