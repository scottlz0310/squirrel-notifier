<#
.SYNOPSIS
    x64 の publish、MSI、setup ZIP、checksum を同じ手順で生成します。
#>
[CmdletBinding()]
param(
    [string]$Version,

    [ValidateSet('x64')]
    [string]$Platform = 'x64',

    [string]$OutputDirectory = (Join-Path (Get-Location) 'release-output')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repoRoot 'winui3\SquirrelNotifier.WinUI3\SquirrelNotifier.WinUI3.csproj'
$installerProject = Join-Path $repoRoot 'winui3\SquirrelNotifier.Installer\SquirrelNotifier.Installer.wixproj'
$majorUpgradeScript = Join-Path $repoRoot 'scripts\test-msi-major-upgrade.ps1'
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
$publishDirectory = Join-Path $outputRoot "publish\$Platform"
$msiDirectory = Join-Path $outputRoot "msi\$Platform"
$bundleDirectory = Join-Path $outputRoot "installer\$Platform"

function Get-ProjectVersion {
    [xml]$project = Get-Content -LiteralPath $projectPath -Raw -Encoding utf8
    $versions = @($project.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    if ($versions.Count -ne 1) {
        throw 'csproj から release version を一意に取得できません。'
    }

    return [string]$versions[0]
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-ProjectVersion
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "release version が SemVer の 3 要素形式ではありません: $Version"
}

if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    throw 'WiX CLI が見つかりません。先に dotnet tool install --global wix --version 7.0.0 を実行してください。'
}
$wixVersion = (& wix --version 2>$null | Select-Object -First 1 | Out-String).Trim()
if ($wixVersion -notmatch '^7\.') {
    throw "WiX CLI 7.x が必要です。検出値: $wixVersion"
}

New-Item -ItemType Directory -Path $publishDirectory, $msiDirectory, $bundleDirectory -Force | Out-Null

dotnet publish $projectPath `
    --configuration Release `
    --runtime "win-$Platform" `
    --self-contained true `
    --output $publishDirectory `
    /p:Platform=$Platform `
    /p:Version=$Version `
    /p:PublishSingleFile=false `
    /p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) {
    throw 'release publish に失敗しました。'
}

dotnet build $installerProject `
    --configuration Release `
    /p:Platform=$Platform `
    /p:HarvestPath=$publishDirectory `
    /p:PackageVersion=$Version `
    /p:OutputPath=$msiDirectory
if ($LASTEXITCODE -ne 0) {
    throw 'MSI build に失敗しました。'
}

$msi = Get-ChildItem -LiteralPath $msiDirectory -Filter '*.msi' -File -Recurse | Sort-Object FullName | Select-Object -First 1
if ($null -eq $msi) {
    throw "MSI が見つかりません: $msiDirectory"
}
& pwsh -NoProfile -File $majorUpgradeScript -MsiPath $msi.FullName
if ($LASTEXITCODE -ne 0) {
    throw 'MSI MajorUpgrade 検証に失敗しました。'
}

Copy-Item -Path (Join-Path $publishDirectory '*') -Destination $bundleDirectory -Recurse -Force
Copy-Item -LiteralPath @(
    (Join-Path $repoRoot 'scripts\install.ps1'),
    (Join-Path $repoRoot 'scripts\install.cmd'),
    (Join-Path $repoRoot 'scripts\uninstall.ps1'),
    (Join-Path $repoRoot 'scripts\uninstall.cmd'),
    (Join-Path $repoRoot 'scripts\create-shortcuts.ps1'),
    (Join-Path $repoRoot 'scripts\create-shortcuts.cmd')
) -Destination $bundleDirectory -Force

$archiveName = "SquirrelNotifier-Setup-$Version-$Platform.zip"
$archivePath = Join-Path $outputRoot $archiveName
Compress-Archive -Path (Join-Path $bundleDirectory '*') -DestinationPath $archivePath -Force

$msiName = "SquirrelNotifier-Setup-$Version-$Platform.msi"
$msiPath = Join-Path $outputRoot $msiName
Copy-Item -LiteralPath $msi.FullName -Destination $msiPath -Force

$checksumName = "checksums-$Platform.txt"
$checksumPath = Join-Path $outputRoot $checksumName
$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
$msiHash = (Get-FileHash -LiteralPath $msiPath -Algorithm SHA256).Hash
$checksumLines = @(
    "$archiveHash  $archiveName"
    "$msiHash  $msiName"
)
$checksumLines | Set-Content -LiteralPath $checksumPath -Encoding ascii

$manifest = [ordered]@{
    schemaVersion = 1
    version = $Version
    platform = $Platform
    publishDirectory = [System.IO.Path]::GetRelativePath($outputRoot, $publishDirectory)
    installerArchive = $archiveName
    msi = $msiName
    checksums = $checksumName
    createdAt = [DateTimeOffset]::UtcNow.ToString('O')
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputRoot 'release-package.json') -Encoding utf8

Write-Host "release package を生成しました: $outputRoot"
