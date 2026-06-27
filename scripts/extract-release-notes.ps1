<#
.SYNOPSIS
  CHANGELOG.md の指定バージョン節を抽出し、定型テンプレートと合成してリリースノートを生成する。
.DESCRIPTION
  リリースページの原稿は CHANGELOG.md を単一の真実の源とする。
  本スクリプトは該当バージョンの節を抽出し、インストール手順・前提条件などの
  定型フッタ（.github/RELEASE_TEMPLATE.md）を {{VERSION}} 差し込みで合成する。
.EXAMPLE
  ./scripts/extract-release-notes.ps1 -Version 0.1.3
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$ChangelogPath = "CHANGELOG.md",
    [string]$TemplatePath  = ".github/RELEASE_TEMPLATE.md",
    [string]$OutPath       = "release-notes.md",
    [string]$Repo          = "scottlz0310/squirrel-notifier"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $ChangelogPath)) {
    throw "CHANGELOG が見つかりません: $ChangelogPath"
}

$lines = Get-Content -LiteralPath $ChangelogPath
$escaped = [regex]::Escape($Version)
$headingPattern    = "^##\s*\[$escaped\]"
$anyVersionPattern = "^##\s*\["

$collected = New-Object System.Collections.Generic.List[string]
$inSection = $false
$prevVersion = $null
foreach ($line in $lines) {
    if ($line -match $headingPattern) { $inSection = $true; continue }
    if ($inSection -and $line -match $anyVersionPattern) {
        if ($line -match "^##\s*\[([0-9][^\]]*)\]") { $prevVersion = $Matches[1] }
        break
    }
    if ($inSection) { $collected.Add($line) }
}

if (-not $inSection) { throw "CHANGELOG に [$Version] の節がありません" }

$section = ($collected -join "`n").Trim()
if ([string]::IsNullOrWhiteSpace($section)) { throw "[$Version] の節が空です" }

$parts = New-Object System.Collections.Generic.List[string]
$parts.Add("## Squirrel Notifier v$Version")
$parts.Add($section)

if (Test-Path -LiteralPath $TemplatePath) {
    $footer = (Get-Content -LiteralPath $TemplatePath -Raw).Replace("{{VERSION}}", $Version).Trim()
    if ($footer) { $parts.Add($footer) }
}

if ($prevVersion) {
    $parts.Add("---`n`n**Full Changelog**: https://github.com/$Repo/compare/v$prevVersion...v$Version")
}

$out = ($parts -join "`n`n") + "`n"

$outFull = if ([System.IO.Path]::IsPathRooted($OutPath)) { $OutPath } else { Join-Path (Get-Location).Path $OutPath }
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($outFull, $out, $utf8NoBom)
Write-Host "リリースノートを生成しました: $outFull (前バージョン: $prevVersion)"
