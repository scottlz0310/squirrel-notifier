<#
.SYNOPSIS
  CHANGELOG.md の指定バージョン節を抽出し、house style に整形してリリースノートを生成する。
.DESCRIPTION
  リリースページの原稿は CHANGELOG.md を単一の真実の源とする。
  - バージョン見出し直下の自由文（任意）を導入文として扱う
  - ### セクション名を日本語へマップし `## 変更内容` 配下にまとめる
  - .github/RELEASE_TEMPLATE.md（インストール手順・前提条件）を {{VERSION}} 差し込みで付与
  - 前バージョンは semver 上「対象より小さい中の最大」を採用（無ければ compare リンク省略）
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

# --- 対象バージョン節の抽出 + 全バージョン収集 ---
$escaped = [regex]::Escape($Version)
$headingPattern    = "^##\s*\[$escaped\]"
$anyVersionPattern = "^##\s*\["

$collected   = New-Object System.Collections.Generic.List[string]
$allVersions = New-Object System.Collections.Generic.List[object]
$inSection = $false
foreach ($line in $lines) {
    if ($line -match "^##\s*\[([0-9][^\]]*)\]") {
        $raw = $Matches[1].Trim()
        $parsed = $null
        if ([version]::TryParse($raw, [ref]$parsed)) {
            $allVersions.Add([pscustomobject]@{ Raw = $raw; Ver = $parsed })
        }
    }
    if ($line -match $headingPattern) { $inSection = $true; continue }
    # 次のバージョン見出し、またはフッタのリンク参照定義（[x]: http...）で停止
    if ($inSection -and ($line -match $anyVersionPattern -or $line -match "^\[[^\]]+\]:")) { break }
    if ($inSection) { $collected.Add($line) }
}

if (-not $inSection) { throw "CHANGELOG に [$Version] の節がありません" }

# --- 導入文（最初の ### より前の自由文）と セクション群 に分割 ---
$introLines   = New-Object System.Collections.Generic.List[string]
$sectionLines = New-Object System.Collections.Generic.List[string]
$inBody = $false
foreach ($line in $collected) {
    if (-not $inBody -and $line -match "^###\s") { $inBody = $true }
    if ($inBody) { $sectionLines.Add($line) } else { $introLines.Add($line) }
}
$intro = ($introLines -join "`n").Trim()

# --- セクション名の日本語化 ---
$map = @{
    "Added"         = "追加"
    "Changed"       = "変更"
    "Deprecated"    = "非推奨"
    "Removed"       = "削除"
    "Fixed"         = "修正"
    "Security"      = "セキュリティ"
    "Documentation" = "ドキュメント"
    "Technical"     = "技術"
}
$mappedSections = foreach ($line in $sectionLines) {
    $m = [regex]::Match($line, "^###\s+(.+?)\s*$")
    if ($m.Success -and $map.ContainsKey($m.Groups[1].Value)) {
        "### " + $map[$m.Groups[1].Value]
    } else {
        $line
    }
}
$sections = (($mappedSections) -join "`n").Trim()
if ([string]::IsNullOrWhiteSpace($sections)) { throw "[$Version] のセクションが空です" }

# --- 前バージョン（semver 上、対象より小さい中の最大。版系列境界の誤検出を防止） ---
$prevVersion = $null
$target = $null
if ([version]::TryParse($Version, [ref]$target)) {
    $pred = $allVersions | Where-Object { $_.Ver -lt $target } |
            Sort-Object Ver -Descending | Select-Object -First 1
    if ($pred) { $prevVersion = $pred.Raw }
}

# --- 合成 ---
$parts = New-Object System.Collections.Generic.List[string]
$parts.Add("## Squirrel Notifier v$Version")
if ($intro) {
    $parts.Add($intro)
    $parts.Add("---")
}
$parts.Add("## 変更内容")
$parts.Add($sections)

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
