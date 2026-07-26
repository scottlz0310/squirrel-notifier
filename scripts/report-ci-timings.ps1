<#
.SYNOPSIS
  GitHub Actions run の job / step 所要時間を job summary へ出力する。
.DESCRIPTION
  CI の壁時計を継続的に観測するための計測ステップ（#220）。
  - 対象 run attempt の job 一覧を GitHub API から取得する
  - job ごとの所要時間と step 内訳を markdown 表として出力する
  - run 開始から最後に完了した job までを「壁時計」として記録する

  壁時計には本レポート job 自身は含めない。計測を追加したことで baseline との
  比較が崩れないようにするため。
.EXAMPLE
  ./scripts/report-ci-timings.ps1 -Repository scottlz0310/squirrel-notifier -RunId 123 -RunAttempt 1
#>
param(
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$RunId,
    [int]$RunAttempt = 1,
    [string]$SummaryPath = $env:GITHUB_STEP_SUMMARY,
    # 壁時計から除外する job。既定は本 script を実行している job 自身。
    [string]$ExcludeJobName = ($env:GITHUB_JOB ?? "timing-report")
)

$ErrorActionPreference = "Stop"

function Invoke-GitHubApi {
    param([string]$Path, [string]$Jq)

    $raw = gh api $Path --jq $Jq
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub API 呼び出しに失敗しました (exit $LASTEXITCODE): $Path"
    }
    return ($raw -join "`n") | ConvertFrom-Json
}

function Get-DurationSeconds {
    param($Started, $Completed)

    if (-not $Started -or -not $Completed) { return $null }
    $from = [datetimeoffset]::Parse($Started, [cultureinfo]::InvariantCulture)
    $to = [datetimeoffset]::Parse($Completed, [cultureinfo]::InvariantCulture)
    return [int][math]::Round(($to - $from).TotalSeconds)
}

function Format-Duration {
    param($Seconds)

    if ($null -eq $Seconds) { return "-" }
    if ($Seconds -lt 60) { return "{0}s" -f $Seconds }
    return "{0}m{1:d2}s" -f [math]::Floor($Seconds / 60), ($Seconds % 60)
}

$attemptPath = "repos/$Repository/actions/runs/$RunId/attempts/$RunAttempt"
# jq の出力を ConvertFrom-Json へ渡すため、スカラーではなく JSON object へ包む。
$runStartedAt = (Invoke-GitHubApi -Path $attemptPath -Jq "{runStartedAt: .run_started_at}").runStartedAt
$jobs = @(Invoke-GitHubApi -Path "${attemptPath}/jobs?per_page=100" -Jq ".jobs")

$measured = @($jobs | Where-Object { $_.name -ne $ExcludeJobName } | Sort-Object { $_.started_at })

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("## CI 所要時間 (run $RunId / attempt $RunAttempt)")
$lines.Add("")
$lines.Add("| Job | 所要時間 | 結果 |")
$lines.Add("|---|---:|---|")

foreach ($job in $measured) {
    $seconds = Get-DurationSeconds -Started $job.started_at -Completed $job.completed_at
    $lines.Add(("| {0} | {1} | {2} |" -f $job.name, (Format-Duration $seconds), ($job.conclusion ?? "-")))
}

$completedAt = @($measured | Where-Object { $_.completed_at } | ForEach-Object {
        [datetimeoffset]::Parse($_.completed_at, [cultureinfo]::InvariantCulture)
    })
if ($completedAt.Count -eq 0) {
    throw "完了した job がありません。壁時計を算出できません: $attemptPath"
}
$runStart = [datetimeoffset]::Parse($runStartedAt, [cultureinfo]::InvariantCulture)
$lastCompleted = $completedAt | Sort-Object | Select-Object -Last 1
$wallClock = [int][math]::Round(($lastCompleted - $runStart).TotalSeconds)

$lines.Add(("| **壁時計（{0} を除く）** | **{1}** | |" -f $ExcludeJobName, (Format-Duration $wallClock)))
$lines.Add("")

foreach ($job in $measured) {
    $lines.Add(("<details><summary>{0} のステップ内訳</summary>" -f $job.name))
    $lines.Add("")
    $lines.Add("| # | Step | 所要時間 | 結果 |")
    $lines.Add("|---:|---|---:|---|")
    foreach ($step in $job.steps) {
        $seconds = Get-DurationSeconds -Started $step.started_at -Completed $step.completed_at
        $lines.Add(("| {0} | {1} | {2} | {3} |" -f $step.number, $step.name, (Format-Duration $seconds), ($step.conclusion ?? "-")))
    }
    $lines.Add("")
    $lines.Add("</details>")
    $lines.Add("")
}

$report = $lines -join "`n"
Write-Host $report

if ($SummaryPath) {
    Add-Content -LiteralPath $SummaryPath -Value $report -Encoding utf8
}
