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
    $csFiles = @($zip.Entries |
            Where-Object { $_.FullName.EndsWith(".cs", [StringComparison]::OrdinalIgnoreCase) } |
            ForEach-Object { $_.FullName } |
            Sort-Object)
}
finally {
    $zip.Dispose()
}

# obj 配下と *.g.cs / *.g.i.cs はビルド生成物。手書きソースの抽出漏れを見るため分けて数える。
$generated = @($csFiles | Where-Object { $_ -match '/obj/' -or $_ -match '\.g\.cs$' -or $_ -match '\.g\.i\.cs$' })
$handwritten = @($csFiles | Where-Object { $generated -notcontains $_ })

$lines.Add("### 抽出された C# ソース")
$lines.Add("")
$lines.Add("| 区分 | 件数 |")
$lines.Add("|---|---:|")
$lines.Add(("| 合計 .cs | {0} |" -f $csFiles.Count))
$lines.Add(("| 手書き | {0} |" -f $handwritten.Count))
$lines.Add(("| 生成コード（obj / *.g.cs） | {0} |" -f $generated.Count))
$lines.Add("")

$listHash = [System.BitConverter]::ToString(
    [System.Security.Cryptography.SHA256]::HashData(
        [System.Text.Encoding]::UTF8.GetBytes(($handwritten -join "`n")))).Replace("-", "").ToLowerInvariant()
$lines.Add("手書きソース一覧の SHA256: ``$listHash``")
$lines.Add("")

$lines.Add("<details><summary>手書き C# ソース一覧</summary>")
$lines.Add("")
$lines.Add('```')
$handwritten | ForEach-Object { $lines.Add($_) }
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
