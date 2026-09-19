<#
.SYNOPSIS
    WiX CLI 7.x を現在の runner へ冪等に用意します。
#>
[CmdletBinding()]
param(
    [string]$Version = '7.0.0'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$toolDirectory = Join-Path $env:USERPROFILE '.dotnet\tools'
if (Test-Path -LiteralPath $toolDirectory -PathType Container) {
    if ($env:PATH -notlike "*$toolDirectory*") {
        $env:PATH = "$toolDirectory;$env:PATH"
    }
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_PATH)) {
        Add-Content -LiteralPath $env:GITHUB_PATH -Value $toolDirectory
    }
}

$wix = Get-Command wix -ErrorAction SilentlyContinue
$currentVersion = if ($null -eq $wix) { $null } else { (& $wix.Source --version 2>$null | Select-Object -First 1 | Out-String).Trim() }
$versionPattern = "(?m)(^|[^0-9])$([regex]::Escape($Version))([^0-9]|$)"
$toolOperationExitCode = 0

if ($null -eq $wix) {
    dotnet tool install --global wix --version $Version
    $toolOperationExitCode = $LASTEXITCODE
}
elseif ($currentVersion -notmatch $versionPattern) {
    dotnet tool update --global wix --version $Version
    $toolOperationExitCode = $LASTEXITCODE
}

if ($toolOperationExitCode -ne 0) {
    throw "WiX CLI $Version のインストールまたは更新に失敗しました。"
}

$wix = Get-Command wix -ErrorAction SilentlyContinue
if ($null -eq $wix) {
    throw 'WiX CLI を PATH から解決できません。'
}

$verifiedVersion = (& $wix.Source --version 2>$null | Select-Object -First 1 | Out-String).Trim()
if ($verifiedVersion -notmatch $versionPattern) {
    throw "WiX CLI の version が期待値と異なります。期待値=$Version 検出値=$verifiedVersion"
}

Write-Host "WiX CLI を準備しました: $verifiedVersion"
