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

function Get-CommandVersion {
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    $command = Get-Command $Name -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $command) {
        return [ordered]@{ available = $false; path = $null; version = $null }
    }

    $version = $null
    try {
        $version = (& $command.Source --version 2>$null | Select-Object -First 1 | Out-String).Trim()
    }
    catch {
        $version = $null
    }

    return [ordered]@{
        available = $true
        path = $command.Source
        version = if ([string]::IsNullOrWhiteSpace($version)) { $null } else { $version }
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

if ($docker.available) {
    $dockerDiskUsage = (& docker system df --format '{{json .}}' 2>$null | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}
else {
    $dockerDiskUsage = @()
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
}

New-Item -ItemType Directory -Path (Split-Path -Parent $outputFullPath) -Force | Out-Null
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $outputFullPath -Encoding utf8
Write-Host "Phase 2 storage measurement: $outputFullPath"
Write-Host "measuredRequiredBytes=$requiredBytes"
if ($null -ne $result.budget.recommendedMinimumGiB) {
    Write-Host "recommendedMinimumGiB=$($result.budget.recommendedMinimumGiB)"
}
