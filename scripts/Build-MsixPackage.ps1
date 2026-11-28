<#
.SYNOPSIS
    アプリのビルド出力（発行物）から MSIX パッケージを生成します（#569）。

.DESCRIPTION
    マニフェストとロゴは winui3/SquirrelNotifier.Msix を使い、版は csproj の <Version> に合わせます。
    makeappx / signtool は、winui3/Directory.Packages.props が指定する
    Microsoft.Windows.SDK.BuildTools の NuGet パッケージから解決します。

.PARAMETER PayloadDirectory
    SquirrelNotifier.WinUI3.exe を含むディレクトリ（dotnet publish の出力など）。

.PARAMETER CertificateThumbprint
    CurrentUser\My にある署名証明書の拇印。件名はマニフェストの Publisher と一致させます。
    省略した場合は署名しません（そのままでは導入できません）。

.PARAMETER TimestampUrl
    RFC 3161 のタイムスタンプサーバー。署名する場合だけ指定できます。

.EXAMPLE
    ./scripts/Build-MsixPackage.ps1 -PayloadDirectory release-output/publish/x64
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PayloadDirectory,
    [string]$OutputDirectory = (Join-Path (Get-Location) 'msix-output'),
    [string]$Version,
    [string]$CertificateThumbprint,
    [string]$TimestampUrl,
    [string]$MakeAppxPath,
    [string]$SignToolPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$msixRoot = Join-Path $repoRoot 'winui3' 'SquirrelNotifier.Msix'
$projectPath = Join-Path $repoRoot 'winui3' 'SquirrelNotifier.WinUI3' 'SquirrelNotifier.WinUI3.csproj'

function Resolve-SdkTool([string]$Name) {
    $propsPath = Join-Path $repoRoot 'winui3' 'Directory.Packages.props'
    [xml]$props = Get-Content -LiteralPath $propsPath -Raw -Encoding utf8
    $node = $props.SelectSingleNode("//PackageVersion[@Include='Microsoft.Windows.SDK.BuildTools']")
    if ($null -eq $node) {
        throw "Microsoft.Windows.SDK.BuildTools の版が見つかりません: $propsPath"
    }

    $nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget' 'packages' }
    $binDirectory = Join-Path $nugetRoot 'microsoft.windows.sdk.buildtools' $node.Version 'bin'
    $found = @(Get-ChildItem -Path (Join-Path $binDirectory '*' 'x64' $Name) -File -ErrorAction SilentlyContinue)
    if ($found.Count -ne 1) {
        throw "$Name が見つかりません: $binDirectory（dotnet restore を実行してください）"
    }

    return $found[0].FullName
}

# PE ヘッダーから対象アーキテクチャ（IMAGE_FILE_HEADER.Machine）を読む。
function Get-PeMachine([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $reader = [System.IO.BinaryReader]::new($stream)
        if ($stream.Length -ge 0x40 -and $reader.ReadUInt16() -eq 0x5A4D) {
            $stream.Position = 0x3C
            $headerOffset = $reader.ReadInt32()
            if ($headerOffset -ge 0 -and $headerOffset + 6 -le $stream.Length) {
                $stream.Position = $headerOffset
                if ($reader.ReadUInt32() -eq 0x00004550) {
                    return $reader.ReadUInt16()
                }
            }
        }
    }
    finally {
        $stream.Dispose()
    }

    throw "PE 形式の実行ファイルではありません: $Path"
}

function Invoke-SdkTool([string]$Path, [string[]]$Arguments, [string]$Failure) {
    $global:LASTEXITCODE = 0
    $output = & $Path @Arguments 2>&1
    if ($global:LASTEXITCODE -ne 0) {
        throw "$Failure（終了コード $global:LASTEXITCODE）:`n$($output -join "`n")"
    }
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = & (Join-Path $PSScriptRoot 'Get-ProjectVersion.ps1') -ProjectPath $projectPath
}
& (Join-Path $PSScriptRoot 'Assert-ReleaseVersion.ps1') -Version $Version

if ($TimestampUrl -and -not $CertificateThumbprint) {
    throw 'TimestampUrl は CertificateThumbprint と併せて指定してください。'
}

[xml]$manifest = Get-Content -LiteralPath (Join-Path $msixRoot 'AppxManifest.xml') -Raw -Encoding utf8
$executable = $manifest.Package.Applications.Application.Executable
$executablePath = Join-Path $PayloadDirectory $executable
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw "発行物に $executable がありません: $PayloadDirectory"
}

# 配布は x64 だけ。makeappx は中身を検査しないため、別のアーキテクチャの発行物を生成前に拒否する。
$architecture = $manifest.Package.Identity.ProcessorArchitecture
if ($architecture -ne 'x64') {
    throw "対応していないアーキテクチャです: $architecture"
}
if ((Get-PeMachine (Resolve-Path -LiteralPath $executablePath).ProviderPath) -ne 0x8664) {
    throw "x64 の実行ファイルではありません: $executablePath"
}

if (-not $MakeAppxPath) { $MakeAppxPath = Resolve-SdkTool 'makeappx.exe' }
if ($CertificateThumbprint -and -not $SignToolPath) { $SignToolPath = Resolve-SdkTool 'signtool.exe' }

$outputRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$packageName = "SquirrelNotifier-$Version-$architecture.msix"
$packagePath = Join-Path $outputRoot $packageName
# 生成と署名は作業ディレクトリで行い、すべて成功してから最終パスを置き換える。
# 失敗しても、同じ版の既存パッケージは残る。
$workDirectory = Join-Path $outputRoot "work-$([guid]::NewGuid().ToString('N'))"
$stagingDirectory = Join-Path $workDirectory 'content'
$workPackage = Join-Path $workDirectory $packageName

try {
    New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null
    Copy-Item -Path (Join-Path $PayloadDirectory '*') -Destination $stagingDirectory -Recurse -Force
    $assetsDirectory = Join-Path $stagingDirectory 'Assets'
    New-Item -ItemType Directory -Path $assetsDirectory -Force | Out-Null
    Copy-Item -Path (Join-Path $msixRoot 'Assets' '*') -Destination $assetsDirectory -Force

    # MSIX の版は 4 要素。4 番目は 0 に固定する。
    $manifest.Package.Identity.Version = "$Version.0"
    $manifest.Save((Join-Path $stagingDirectory 'AppxManifest.xml'))

    Invoke-SdkTool $MakeAppxPath @('pack', '/d', $stagingDirectory, '/p', $workPackage, '/o') 'MSIX の生成に失敗しました'

    if ($CertificateThumbprint) {
        $signArguments = @('sign', '/fd', 'SHA256', '/sha1', $CertificateThumbprint)
        if ($TimestampUrl) { $signArguments += @('/tr', $TimestampUrl, '/td', 'SHA256') }
        Invoke-SdkTool $SignToolPath ($signArguments + $workPackage) 'MSIX の署名に失敗しました'
    }

    [System.IO.File]::Move($workPackage, $packagePath, $true)
}
finally {
    if (Test-Path -LiteralPath $workDirectory) {
        Remove-Item -LiteralPath $workDirectory -Recurse -Force
    }
}

Write-Host "MSIX を生成しました: $packagePath$(if (-not $CertificateThumbprint) { '（未署名）' })"
return $packagePath
