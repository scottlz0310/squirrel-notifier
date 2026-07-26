<#
.SYNOPSIS
  CodeQL database の抽出範囲・LOC・診断を job summary へ出力する（#220 の build-mode 比較用の一時計測）。
.DESCRIPTION
  manual build-mode と build-mode: none で生成した database を同一ソースに対して比較するための計測。
  - baseline LOC（codeql database print-baseline）
  - src.zip に取り込まれた C# ソースの一覧と件数（手書き / 生成コードを区別）
  - extractor の診断ファイル
.EXAMPLE
  ./scripts/report-codeql-extraction.ps1 -Language csharp
#>
param(
    [string]$Language = "csharp",
    [string]$DatabaseRoot = (Join-Path $env:RUNNER_TEMP "codeql_databases"),
    [string]$SummaryPath = $env:GITHUB_STEP_SUMMARY
)

$ErrorActionPreference = "Stop"

$db = Join-Path $DatabaseRoot $Language
if (-not (Test-Path -LiteralPath $db)) {
    throw "CodeQL database が見つかりません: $db"
}

$codeql = Get-ChildItem -Path "C:\hostedtoolcache\windows\CodeQL" -Recurse -Filter "codeql.exe" -ErrorAction SilentlyContinue |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $codeql) {
    throw "codeql.exe が見つかりません"
}

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("## CodeQL 抽出レポート ($Language)")
$lines.Add("")

$baseline = & $codeql database print-baseline $db 2>&1 | Out-String
$lines.Add("### baseline lines of code")
$lines.Add("")
$lines.Add('```')
$lines.Add($baseline.Trim())
$lines.Add('```')
$lines.Add("")

Add-Type -AssemblyName System.IO.Compression.FileSystem
$srcZip = Join-Path $db "src.zip"
$zip = [System.IO.Compression.ZipFile]::OpenRead($srcZip)
try {
    $entries = @($zip.Entries | Where-Object { $_.FullName.EndsWith(".cs", [StringComparison]::OrdinalIgnoreCase) })
    $records = foreach ($entry in $entries) {
        $reader = New-Object System.IO.StreamReader($entry.Open())
        try { $loc = ($reader.ReadToEnd() -split "`n").Count }
        finally { $reader.Dispose() }

        # 依存パッケージ・SDK 由来のソースは製品コードではないため分けて数える。
        $isExternal = $entry.FullName -match '\.nuget/packages/' -or
        $entry.FullName -match 'hostedtoolcache/' -or
        $entry.FullName -match 'Program Files/'
        $isGenerated = $entry.FullName -match '/obj/' -or $entry.FullName -match '\.g\.i?\.cs$'

        [pscustomobject]@{
            Path     = $entry.FullName
            Loc      = $loc
            Category = if ($isExternal) { "external" } elseif ($isGenerated) { "generated" } else { "repo" }
        }
    }
}
finally {
    $zip.Dispose()
}

$records = @($records | Sort-Object Path)
$repo = @($records | Where-Object Category -EQ "repo")
$generated = @($records | Where-Object Category -EQ "generated")
$external = @($records | Where-Object Category -EQ "external")

function Measure-Loc { param($Set) if ($Set.Count -eq 0) { 0 } else { ($Set | Measure-Object -Property Loc -Sum).Sum } }

$lines.Add("### 抽出された C# ソース")
$lines.Add("")
$lines.Add("| 区分 | ファイル数 | LOC |")
$lines.Add("|---|---:|---:|")
$lines.Add(("| リポジトリ内の手書きソース | {0} | {1} |" -f $repo.Count, (Measure-Loc $repo)))
$lines.Add(("| ビルド生成コード（obj / *.g.cs） | {0} | {1} |" -f $generated.Count, (Measure-Loc $generated)))
$lines.Add(("| 依存パッケージ / SDK 由来 | {0} | {1} |" -f $external.Count, (Measure-Loc $external)))
$lines.Add(("| **合計** | **{0}** | **{1}** |" -f $records.Count, (Measure-Loc $records)))
$lines.Add("")

$listHash = [System.BitConverter]::ToString(
    [System.Security.Cryptography.SHA256]::HashData(
        [System.Text.Encoding]::UTF8.GetBytes((($repo | ForEach-Object { $_.Path }) -join "`n")))).Replace("-", "").ToLowerInvariant()
$lines.Add("リポジトリ内ソース一覧の SHA256: ``$listHash``")
$lines.Add("")

$lines.Add("<details><summary>リポジトリ内の手書き C# ソース一覧</summary>")
$lines.Add("")
$lines.Add('```')
$repo | ForEach-Object { $lines.Add(("{0}`t{1}" -f $_.Loc, $_.Path)) }
$lines.Add('```')
$lines.Add("")
$lines.Add("</details>")
$lines.Add("")

$diagDir = Join-Path $db "diagnostic"
$lines.Add("### extractor 診断")
$lines.Add("")
if (Test-Path -LiteralPath $diagDir) {
    $diagFiles = @(Get-ChildItem -Path $diagDir -Recurse -File -Filter "*.jsonl" -ErrorAction SilentlyContinue)
    if ($diagFiles.Count -eq 0) {
        $lines.Add("診断ファイルなし")
    }
    foreach ($file in $diagFiles) {
        $lines.Add(("- ``{0}``" -f $file.Name))
        $lines.Add('```json')
        Get-Content -LiteralPath $file.FullName | Select-Object -First 40 | ForEach-Object { $lines.Add($_) }
        $lines.Add('```')
    }
}
else {
    $lines.Add("診断ディレクトリなし: $diagDir")
}

$report = $lines -join "`n"
Write-Host $report

if ($SummaryPath) {
    Add-Content -LiteralPath $SummaryPath -Value $report -Encoding utf8
}
