<#
.SYNOPSIS
  csproj の <Version> に対応する節が CHANGELOG.md にあることを確認する。
.DESCRIPTION
  原稿無しでタグが切られる事故を防ぐため、changelog-guard.yml が PR で実行する。
  節の見出しは "## [x.y.z]" で、"[1.2.3]" が "[1.2.30]" に一致しないよう閉じ括弧まで照合する。
.EXAMPLE
  ./scripts/Assert-ChangelogHasVersion.ps1
#>
param(
    [string]$CsprojPath = 'winui3/SquirrelNotifier.WinUI3/SquirrelNotifier.WinUI3.csproj',
    [string]$ChangelogPath = 'CHANGELOG.md'
)

$ErrorActionPreference = 'Stop'

$xml = [xml](Get-Content -LiteralPath $CsprojPath -Raw)
$node = $xml.SelectSingleNode("//Version")
if (-not $node) { throw "csproj から <Version> を取得できません" }
$version = $node.InnerText.Trim()
Write-Host "csproj version: $version"

$escaped = [regex]::Escape($version)
$found = Get-Content -LiteralPath $ChangelogPath | Select-String -Pattern "^##\s*\[$escaped\]" -Quiet
if (-not $found) {
    throw "CHANGELOG.md に [$version] の節がありません。リリース準備 PR では CHANGELOG を更新してください。"
}
Write-Host "OK: CHANGELOG に [$version] の節があります"
