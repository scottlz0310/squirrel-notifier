<#
.SYNOPSIS
  タグを作る前に、リリースコミットのタイトルと csproj の安定版が一致することを確認する。
#>
param(
    [Parameter(Mandatory)][string]$Message,
    [string]$CsprojPath = 'winui3/SquirrelNotifier.WinUI3/SquirrelNotifier.WinUI3.csproj'
)

$ErrorActionPreference = 'Stop'
$title = ($Message -split '\r?\n')[0]
if ($title -cnotmatch '^chore\(release\): v([0-9]+\.[0-9]+\.[0-9]+)$') {
    throw 'リリースタイトルは chore(release): vX.Y.Z の安定版形式である必要があります。'
}
$version = $Matches[1]
& (Join-Path $PSScriptRoot 'Assert-ReleaseVersion.ps1') -Version $version
[xml]$project = Get-Content -LiteralPath $CsprojPath -Raw
$versions = @($project.SelectNodes('//Version') | ForEach-Object { $_.InnerText.Trim() })
if ($versions.Count -ne 1 -or $versions[0] -cne $version) {
    throw "リリースタイトルの version と csproj が一致しません: $version"
}
& (Join-Path $PSScriptRoot 'Assert-ChangelogHasVersion.ps1') -CsprojPath $CsprojPath
