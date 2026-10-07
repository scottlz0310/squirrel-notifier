<#
.SYNOPSIS
  ランブックの 1 シナリオ分の隔離環境を用意し、対象ビルドの Squirrel Notifier を起動する（#433）。
.DESCRIPTION
  - 作業ツリーがクリーンで、起動する exe の ProductVersion が HEAD の commit SHA を含むことを確かめる
  - データディレクトリ（SQUIRREL_NOTIFIER_DATA_ROOT）、fake Gateway、dummy subscriber、dummy launcher を
    シナリオ専用の run ディレクトリに閉じ込める。本番の gateway・queue・PR・資格情報には触れない
  - 利用者の実データ（%LOCALAPPDATA%\SquirrelNotifier）の状態を記録し、Stop-RunbookEnvironment.ps1 が
    漏れの有無を判定する材料にする
  結果は run ディレクトリの run.json に書き、同じ内容を標準出力へ JSON で出す。
.EXAMPLE
  pwsh -File tests/e2e/agent-runbooks/tools/Start-RunbookEnvironment.ps1 -Scenario review-event-flow
#>
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]+$')][string]$Scenario,
    [string]$RunRoot = (Join-Path ([IO.Path]::GetTempPath()) 'squirrel-notifier-runbook'),
    [switch]$SkipBuild,
    [int]$SubscriberDelayMs = 5000
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$appProject = Join-Path $repoRoot 'winui3\SquirrelNotifier.WinUI3\SquirrelNotifier.WinUI3.csproj'
$launcherProject = Join-Path $repoRoot 'tests\e2e\fixtures\launcher\DummyLauncher\DummyLauncher.csproj'
$subscriberProject = Join-Path $repoRoot 'tests\e2e\fixtures\subscriber\DummySubscriber\DummySubscriber.csproj'
$runnerProject = Join-Path $repoRoot 'tests\e2e\SquirrelNotifier.HeadlessE2E\SquirrelNotifier.HeadlessE2E.csproj'
$realDataRoot = Join-Path $env:LOCALAPPDATA 'SquirrelNotifier'

function Invoke-Checked([string]$Description, [scriptblock]$Command) {
    # 標準出力は run の JSON だけにするため、build の出力は標準エラーへ回す
    & $Command 2>&1 | ForEach-Object { [Console]::Error.WriteLine($_) }
    if ($LASTEXITCODE -ne 0) {
        throw "$Description に失敗しました（exit $LASTEXITCODE）。"
    }
}

function Resolve-BuildOutput([string]$Project, [string]$FileName, [string]$Pattern) {
    $file = Get-ChildItem -LiteralPath (Join-Path (Split-Path $Project) 'bin') -Recurse -Filter $FileName -File |
        Where-Object { $_.FullName -like $Pattern } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $file) {
        throw "build output から $FileName を解決できません（$Pattern）。"
    }
    $file.FullName
}

$commit = (git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'HEAD の commit SHA を取得できません。' }
$dirty = git -C $repoRoot status --porcelain --untracked-files=no
if ($dirty) {
    throw "作業ツリーに未コミットの変更があります。テスト対象の commit を特定できないため中止します:`n$($dirty -join "`n")"
}

if (-not $SkipBuild) {
    Invoke-Checked 'アプリの build' { dotnet build $appProject -c Release -p:Platform=x64 -v q -nologo }
    Invoke-Checked 'dummy launcher の build' { dotnet build $launcherProject -c Release -p:Platform=x64 -v q -nologo }
    Invoke-Checked 'dummy subscriber の build' { dotnet build $subscriberProject -c Release -p:Platform=x64 -v q -nologo }
    Invoke-Checked 'fake Gateway（headless runner）の build' { dotnet build $runnerProject -c Release -p:Platform=x64 -p:IncludeWindowsSdkBuildTools=false -v q -nologo }
}

$appPath = Resolve-BuildOutput $appProject 'SquirrelNotifier.WinUI3.exe' '*\bin\x64\Release\net10.0-windows*\SquirrelNotifier.WinUI3.exe'
$launcherPath = Resolve-BuildOutput $launcherProject 'codex.exe' '*\x64\Release\net10.0\win-x64\codex.exe'
$subscriberPath = Resolve-BuildOutput $subscriberProject 'resource-bridge-cli.exe' '*\x64\Release\net10.0\win-x64\resource-bridge-cli.exe'
$gatewayHostPath = Resolve-BuildOutput $runnerProject 'SquirrelNotifier.HeadlessE2E.exe' '*\x64\Release\net10.0-windows*\win-x64\SquirrelNotifier.HeadlessE2E.exe'

# 古いビルドを起動すると、データディレクトリの切り替えが効かず実データへ書き込みうるため、起動前に止める
$productVersion = (Get-Item -LiteralPath $appPath).VersionInfo.ProductVersion
if ($productVersion -notmatch "\+$commit$") {
    throw "exe の ProductVersion（$productVersion）が HEAD（$commit）と一致しません。-SkipBuild を外して build し直してください: $appPath"
}

$runId = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $Scenario
$runDirectory = Join-Path $RunRoot $runId
$dataRoot = Join-Path $runDirectory 'data'
$evidenceDirectory = Join-Path $runDirectory 'evidence'
$observationDirectory = Join-Path $runDirectory 'observations'
New-Item -ItemType Directory -Force -Path $dataRoot, $evidenceDirectory, $observationDirectory | Out-Null

# 実データの状態。ログは常駐インスタンスが書き続けるため、長さだけ記録して追記分を後で検査する
$realLog = Join-Path $realDataRoot 'logs\winui3.log'
$realState = [ordered]@{
    root = $realDataRoot
    logLength = if (Test-Path -LiteralPath $realLog) { (Get-Item -LiteralPath $realLog).Length } else { 0 }
    settingsHash = if (Test-Path -LiteralPath (Join-Path $realDataRoot 'settings.json')) { (Get-FileHash -LiteralPath (Join-Path $realDataRoot 'settings.json')).Hash } else { $null }
}

$endpointFile = Join-Path $runDirectory 'gateway-endpoint.txt'
$gateway = Start-Process -FilePath $gatewayHostPath -ArgumentList @('serve-gateway', '--endpoint-file', "`"$endpointFile`"", '--lifetime-minutes', '120') -PassThru -WindowStyle Hidden
$deadline = (Get-Date).AddSeconds(30)
while (-not (Test-Path -LiteralPath $endpointFile)) {
    if ($gateway.HasExited -or (Get-Date) -ge $deadline) {
        throw 'fake Gateway が起動しませんでした。'
    }
    Start-Sleep -Milliseconds 200
}
$gatewayUrl = (Get-Content -LiteralPath $endpointFile -Raw).Trim()

$migrationFlags = @(
    'LauncherSlotsMigrated', 'ReviewedLauncherSkillMigrated', 'AgyPrintTimeoutMigrated',
    'CodexReviewerWorkingDirectoryMigrated', 'ClaudeStreamJsonMigrated', 'CodexJsonOutputMigrated',
    'AgyStreamJsonMigrated', 'LauncherSkillPromptMigrated', 'CodexSkillPromptPrefixMigrated',
    'LauncherPresetsMigrated', 'LauncherResumeTemplatesMigrated'
)
$settings = [ordered]@{
    SubscriberCommandPath = $subscriberPath
    GatewayUrl = $gatewayUrl
    ResourceUri = 'queue://review/queue'
    ResourceUris = @('queue://review/queue')
    NotificationTimeoutMs = 60000
    ReviewerLauncherPresetId = 'custom'
    ReviewerLauncherCommandPath = $launcherPath
    ReviewerLauncherArguments = 'review {owner}/{repo}#{prNumber} {reason}'
    ReviewedLauncherPresetId = 'custom'
    ReviewedLauncherCommandPath = $launcherPath
    ReviewedLauncherArguments = 'reviewed {owner}/{repo}#{prNumber}'
    SessionResumeEnabled = $false
    AutoReviewStartEnabled = $false
}
foreach ($flag in $migrationFlags) { $settings[$flag] = $true }
$settings | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $dataRoot 'settings.json') -Encoding utf8NoBOM

# このスクリプトは pwsh -File の別プロセスで動くため、ここで設定した環境変数は呼び出し元のシェルへ残らない。
# アプリは Start-Process で起動し、呼び出し元の標準出力のハンドルを引き継がせない
# （引き継ぐと、パイプラインで受けた呼び出し元がアプリの終了まで待ち続ける）
$env:SQUIRREL_NOTIFIER_DATA_ROOT = $dataRoot
$env:SQUIRREL_NOTIFIER_E2E_SUBSCRIBER_OBSERVATION_PATH = Join-Path $observationDirectory 'subscriber.jsonl'
$env:SQUIRREL_NOTIFIER_E2E_SUBSCRIBER_TOKEN_CACHE_PATH = Join-Path $runDirectory 'token-cache.json'
$env:SQUIRREL_NOTIFIER_E2E_SUBSCRIBER_FLOW = 'review-event-flow'
$env:SQUIRREL_NOTIFIER_E2E_SUBSCRIBER_DELAY_MS = [string]$SubscriberDelayMs
$env:SQUIRREL_NOTIFIER_E2E_DUMMY_OBSERVATION_PATH = Join-Path $observationDirectory 'launcher.jsonl'
# 利用者のシェルに gateway の token があっても、テスト用インスタンスへ渡さない
Remove-Item Env:\MCP_PROBE_AUTH_TOKEN -ErrorAction SilentlyContinue
$app = Start-Process -FilePath $appPath -WorkingDirectory (Split-Path $appPath) -PassThru

$run = [ordered]@{
    schemaVersion = 1
    scenario = $Scenario
    runDirectory = $runDirectory
    commit = $commit
    productVersion = $productVersion
    appPath = $appPath
    appProcessId = $app.Id
    gatewayProcessId = $gateway.Id
    gatewayUrl = $gatewayUrl
    dataRoot = $dataRoot
    evidenceDirectory = $evidenceDirectory
    subscriberObservation = Join-Path $observationDirectory 'subscriber.jsonl'
    launcherObservation = Join-Path $observationDirectory 'launcher.jsonl'
    realData = $realState
    startedAt = (Get-Date).ToUniversalTime().ToString('o')
}
$json = $run | ConvertTo-Json -Depth 5
$json | Set-Content -LiteralPath (Join-Path $runDirectory 'run.json') -Encoding utf8NoBOM
$json
