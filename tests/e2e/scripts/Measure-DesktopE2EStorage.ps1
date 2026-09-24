<#
.SYNOPSIS
    Phase 2 runner の使用量を測定し、EBS volume 候補を比較します。
#>
[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path (Get-Location) 'artifacts\e2e\desktop\storage.json'),

    [string]$WorkspacePath = (Get-Location),

    [int]$HeadroomGiB = 4,

    [int[]]$CandidateSizeGiB = @(32, 40, 48, 64, 80)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-FileBytes {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return 0L
    }

    $measure = Get-ChildItem -LiteralPath $Path -File -Recurse -Force -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum
    if ($null -eq $measure.Sum) {
        return 0L
    }

    return [int64]$measure.Sum
}

function Invoke-MeasuredCommand {
    <#
    .SYNOPSIS
        測定用の外部コマンドを呼び、失敗しても例外にせず終了コードと stderr を返す。
    .DESCRIPTION
        測定は記録が目的なので、docker daemon の未起動などで job を失敗させない。
        代わりに終了コードと stderr を storage.json に残し、run 後に原因を追えるようにする。
    #>
    param(
        [Parameter(Mandatory)]
        [System.Management.Automation.CommandInfo]$Command,

        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $stdout = [System.Collections.Generic.List[string]]::new()
    $stderr = [System.Collections.Generic.List[string]]::new()
    $exitCode = $null
    try {
        foreach ($line in @(& $Command @Arguments 2>&1)) {
            if ($line -is [System.Management.Automation.ErrorRecord]) {
                $stderr.Add($line.ToString())
            }
            else {
                $stdout.Add([string]$line)
            }
        }
        $exitCode = $LASTEXITCODE
    }
    catch {
        $stderr.Add($_.Exception.Message)
    }

    return [ordered]@{
        exitCode = $exitCode
        stdout = @($stdout | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        stderr = @($stderr | Select-Object -First 20)
    }
}

function Get-CommandVersion {
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    $command = Get-Command $Name -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $command) {
        return [ordered]@{ available = $false; path = $null; version = $null; exitCode = $null; stderr = @() }
    }

    $invocation = Invoke-MeasuredCommand -Command $command -Arguments @('--version')
    $version = $invocation.stdout | Select-Object -First 1

    return [ordered]@{
        available = $true
        path = $command.Source
        version = if ([string]::IsNullOrWhiteSpace($version)) { $null } else { $version.Trim() }
        exitCode = $invocation.exitCode
        stderr = $invocation.stderr
    }
}

$workspaceFullPath = [System.IO.Path]::GetFullPath($WorkspacePath)
$outputFullPath = [System.IO.Path]::GetFullPath($OutputPath)
$driveName = ([System.IO.Path]::GetPathRoot($workspaceFullPath)).TrimEnd('\').TrimEnd(':')
$drive = Get-PSDrive -Name $driveName -ErrorAction Stop
$totalBytes = [int64]$drive.Used + [int64]$drive.Free
$usedBytes = [int64]$drive.Used
$headroomBytes = [int64]$HeadroomGiB * 1GB
$requiredBytes = $usedBytes + $headroomBytes
$workspaceBytes = Get-FileBytes -Path $workspaceFullPath
$candidateRows = @(
    foreach ($sizeGiB in ($CandidateSizeGiB | Sort-Object -Unique)) {
        [ordered]@{
            sizeGiB = $sizeGiB
            fitsMeasuredUsage = ([int64]$sizeGiB * 1GB) -ge $requiredBytes
        }
    }
)
$recommendedCandidate = $candidateRows |
    Where-Object fitsMeasuredUsage |
    Select-Object -First 1
$recommendedMinimumGiB = if ($null -eq $recommendedCandidate) {
    $null
}
else {
    $recommendedCandidate.sizeGiB
}
$docker = Get-CommandVersion -Name 'docker'
$dotnet = Get-CommandVersion -Name 'dotnet'
$wix = Get-CommandVersion -Name 'wix'

$dockerDiskUsage = @()
$dockerDiskUsageCommand = $null
if ($docker.available) {
    $invocation = Invoke-MeasuredCommand -Command (Get-Command 'docker' | Select-Object -First 1) -Arguments @('system', 'df', '--format', '{{json .}}')
    $dockerDiskUsage = $invocation.stdout
    $dockerDiskUsageCommand = [ordered]@{
        exitCode = $invocation.exitCode
        stderr = $invocation.stderr
    }
}

$result = [ordered]@{
    schemaVersion = 1
    phase = 'desktop'
    measuredAt = [DateTimeOffset]::UtcNow.ToString('O')
    workspace = [ordered]@{
        path = $workspaceFullPath
        bytes = $workspaceBytes
    }
    systemDrive = [ordered]@{
        name = $driveName
        totalBytes = $totalBytes
        usedBytes = $usedBytes
        freeBytes = [int64]$drive.Free
    }
    budget = [ordered]@{
        headroomGiB = $HeadroomGiB
        measuredRequiredBytes = $requiredBytes
        candidates = $candidateRows
        recommendedMinimumGiB = $recommendedMinimumGiB
    }
    tools = [ordered]@{
        docker = $docker
        dotnet = $dotnet
        wix = $wix
    }
    dockerDiskUsage = @($dockerDiskUsage)
    dockerDiskUsageCommand = $dockerDiskUsageCommand
}

New-Item -ItemType Directory -Path (Split-Path -Parent $outputFullPath) -Force | Out-Null
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $outputFullPath -Encoding utf8
Write-Host "Phase 2 storage measurement: $outputFullPath"
Write-Host "measuredRequiredBytes=$requiredBytes"
if ($null -ne $result.budget.recommendedMinimumGiB) {
    Write-Host "recommendedMinimumGiB=$($result.budget.recommendedMinimumGiB)"
}
$commandExitCodes = [ordered]@{
    'docker --version' = $docker.exitCode
    'dotnet --version' = $dotnet.exitCode
    'wix --version' = $wix.exitCode
    'docker system df' = if ($null -eq $dockerDiskUsageCommand) { $null } else { $dockerDiskUsageCommand.exitCode }
}
foreach ($entry in $commandExitCodes.GetEnumerator()) {
    if ($null -ne $entry.Value -and $entry.Value -ne 0) {
        Write-Warning "$($entry.Key) の終了コードは $($entry.Value) です（stderr は storage.json に記録）。"
    }
}

# 非 0 の終了コードは storage.json へ記録済み。残すと GitHub Actions の shell: pwsh が
# スクリプト末尾で exit $LASTEXITCODE を行い、測定だけで job が失敗する（run 35932153754）。
$global:LASTEXITCODE = 0
