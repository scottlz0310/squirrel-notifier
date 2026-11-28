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
if (-not (Test-Path -LiteralPath (Join-Path $PayloadDirectory $executable) -PathType Leaf)) {
    throw "発行物に $executable がありません: $PayloadDirectory"
}

if (-not $MakeAppxPath) { $MakeAppxPath = Resolve-SdkTool 'makeappx.exe' }
if ($CertificateThumbprint -and -not $SignToolPath) { $SignToolPath = Resolve-SdkTool 'signtool.exe' }

$outputRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$packagePath = Join-Path $outputRoot "SquirrelNotifier-$Version-$($manifest.Package.Identity.ProcessorArchitecture).msix"
$stagingDirectory = Join-Path $outputRoot "staging-$([guid]::NewGuid().ToString('N'))"

try {
    New-Item -ItemType Directory -Path $stagingDirectory | Out-Null
    Copy-Item -Path (Join-Path $PayloadDirectory '*') -Destination $stagingDirectory -Recurse -Force
    $assetsDirectory = Join-Path $stagingDirectory 'Assets'
    New-Item -ItemType Directory -Path $assetsDirectory -Force | Out-Null
    Copy-Item -Path (Join-Path $msixRoot 'Assets' '*') -Destination $assetsDirectory -Force

    # MSIX の版は 4 要素。4 番目は 0 に固定する。
    $manifest.Package.Identity.Version = "$Version.0"
    $manifest.Save((Join-Path $stagingDirectory 'AppxManifest.xml'))

    Invoke-SdkTool $MakeAppxPath @('pack', '/d', $stagingDirectory, '/p', $packagePath, '/o') 'MSIX の生成に失敗しました'

    if ($CertificateThumbprint) {
        $signArguments = @('sign', '/fd', 'SHA256', '/sha1', $CertificateThumbprint)
        if ($TimestampUrl) { $signArguments += @('/tr', $TimestampUrl, '/td', 'SHA256') }
        try {
            Invoke-SdkTool $SignToolPath ($signArguments + $packagePath) 'MSIX の署名に失敗しました'
        }
        catch {
            # 未署名のパッケージを、署名済みの成果物と取り違えないように残さない。
            Remove-Item -LiteralPath $packagePath -Force
            throw
        }
    }
}
finally {
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}

Write-Host "MSIX を生成しました: $packagePath$(if (-not $CertificateThumbprint) { '（未署名）' })"
return $packagePath
