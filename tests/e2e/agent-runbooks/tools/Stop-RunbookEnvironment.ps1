<#
.SYNOPSIS
  Start-RunbookEnvironment.ps1 が起動したプロセスを止め、利用者の実データへの漏れが無いことを確かめる（#433）。
.DESCRIPTION
  - アプリ・fake Gateway と、その子孫プロセス（dummy subscriber / dummy launcher）を止める
  - 実データ（%LOCALAPPDATA%\SquirrelNotifier）に fixture の痕跡が無いことを確かめる。
    常駐中の既定インスタンスもログやキャッシュを書くため、ファイルの変化ではなく、
    fixture だけが持つ文字列（fixture の repository 名・fake Gateway の URL・run ディレクトリ）で判定する
  - 実データの settings.json が開始時から変わっていないことを確かめる
  結果は evidence\isolation.json に書き、漏れがあれば失敗（exit 1）にする。
.EXAMPLE
  pwsh -File tests/e2e/agent-runbooks/tools/Stop-RunbookEnvironment.ps1 -RunDirectory <Start の出力の runDirectory>
#>
param(
    [Parameter(Mandatory)][string]$RunDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$run = Get-Content -LiteralPath (Join-Path $RunDirectory 'run.json') -Raw | ConvertFrom-Json

function Stop-ProcessTree([int]$Id) {
    Get-CimInstance Win32_Process -Filter "ParentProcessId=$Id" | ForEach-Object { Stop-ProcessTree $_.ProcessId }
    Stop-Process -Id $Id -Force -ErrorAction SilentlyContinue
}

Stop-ProcessTree $run.appProcessId
Stop-ProcessTree $run.gatewayProcessId

# 親が先に終了して孤立した dummy プロセスを止める。並行する別の run や、同じ build から起動した
# 別のインスタンスを巻き込まないよう、この run に固有の値（fake Gateway の URL は run ごとにポートが違う、
# run ディレクトリ）をコマンドラインに含むものだけを対象にする
# このスクリプトと呼び出し元のシェルもコマンドラインに run ディレクトリを含みうるため、自分と祖先は除く
$runMarkers = @($run.gatewayUrl.TrimEnd('/'), $run.runDirectory)
$processes = @(Get-CimInstance Win32_Process)
$ancestors = [Collections.Generic.HashSet[uint32]]::new()
$current = [uint32]$PID
while ($current -and $ancestors.Add($current)) {
    $current = ($processes | Where-Object ProcessId -eq $current | Select-Object -First 1).ParentProcessId
}
$leftovers = @($processes | Where-Object {
        $commandLine = $_.CommandLine
        $commandLine -and -not $ancestors.Contains($_.ProcessId) -and
        @($runMarkers | Where-Object { $commandLine.Contains($_, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
    })
foreach ($process in $leftovers) {
    Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
}

$markers = @('fixture-repository', 'fixture-owner', 'second-repository', 'third-repository', $run.gatewayUrl.TrimEnd('/'), $run.runDirectory)
$findings = [Collections.Generic.List[string]]::new()

$realRoot = $run.realData.root
$realLog = Join-Path $realRoot 'logs\winui3.log'
if (Test-Path -LiteralPath $realLog) {
    $stream = [IO.File]::Open($realLog, 'Open', 'Read', 'ReadWrite')
    try {
        $start = [Math]::Min([long]$run.realData.logLength, $stream.Length)
        $stream.Seek($start, 'Begin') | Out-Null
        $appended = [IO.StreamReader]::new($stream).ReadToEnd()
    }
    finally {
        $stream.Dispose()
    }
    foreach ($marker in $markers) {
        if ($appended.Contains($marker, [StringComparison]::OrdinalIgnoreCase)) {
            $findings.Add("実データのログに fixture の痕跡があります: $marker")
        }
    }
}

foreach ($name in 'cache.json', 'review-cycles.json', 'sessions.json', 'statusline-summary.json') {
    $path = Join-Path $realRoot $name
    if (-not (Test-Path -LiteralPath $path)) { continue }
    $content = Get-Content -LiteralPath $path -Raw
    foreach ($marker in $markers) {
        if ($content -and $content.Contains($marker, [StringComparison]::OrdinalIgnoreCase)) {
            $findings.Add("実データの $name に fixture の痕跡があります: $marker")
        }
    }
}

$settingsPath = Join-Path $realRoot 'settings.json'
$settingsHash = if (Test-Path -LiteralPath $settingsPath) { (Get-FileHash -LiteralPath $settingsPath).Hash } else { $null }
if ($settingsHash -ne $run.realData.settingsHash) {
    $findings.Add('実データの settings.json が変更されました。')
}

$isolatedFiles = @(Get-ChildItem -LiteralPath $run.dataRoot -Recurse -File | ForEach-Object { $_.FullName.Substring($run.dataRoot.Length + 1) })
$result = [ordered]@{
    scenario = $run.scenario
    commit = $run.commit
    isolated = $findings.Count -eq 0
    findings = @($findings)
    realSettingsUnchanged = $settingsHash -eq $run.realData.settingsHash
    isolatedDataFiles = $isolatedFiles
    leftoverProcessesStopped = @($leftovers | ForEach-Object { "$($_.ProcessId) $($_.Name)" })
    stoppedAt = (Get-Date).ToUniversalTime().ToString('o')
}
$json = $result | ConvertTo-Json -Depth 4
$json | Set-Content -LiteralPath (Join-Path $run.evidenceDirectory 'isolation.json') -Encoding utf8NoBOM
$json
if ($findings.Count -gt 0) {
    exit 1
}
