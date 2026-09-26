<#
.SYNOPSIS
  指定コミットに対する CI の必須ジョブ（GitHub Actions の check run）が成功していることを確認する。
.DESCRIPTION
  release.yml は tag push で起動するが、ci.yml は tag push では起動しない。そのため公開前に、
  tag が指すコミットに対して ci.yml の必須ジョブが成功していることをここで確かめる（#429 / #426）。
  - 判定対象は GitHub Actions が作成した check run だけ（他 App による同名 check run を採用しない）
  - 同名の再実行は id が最大の run を採用する
  - 未作成・実行中の間は待機し、TimeoutMinutes を超えたら失敗する
  - 失敗・キャンセルなどで完了した run を見つけたら、待たずに失敗する
.EXAMPLE
  ./scripts/Assert-CiChecksPassed.ps1 -Repository scottlz0310/squirrel-notifier -Sha <40桁 SHA>
#>
param(
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$Sha,
    [string[]]$RequiredChecks = @('build-and-test', 'headless-e2e', 'distribution-e2e', 'lint'),
    [int]$TimeoutMinutes = 45,
    [int]$PollSeconds = 60
)

$ErrorActionPreference = 'Stop'

function Get-ActionsCheckRuns {
    $lines = gh api --paginate "repos/$Repository/commits/$Sha/check-runs?per_page=100" `
        --jq '.check_runs[] | {id, name, status, conclusion, app: .app.slug}'
    if ($LASTEXITCODE -ne 0) {
        throw "check run を取得できませんでした: $Repository@$Sha"
    }

    @($lines | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.app -eq 'github-actions' })
}

$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
while ($true) {
    $runs = Get-ActionsCheckRuns
    $pending = @()
    $failed = @()
    foreach ($name in $RequiredChecks) {
        $latest = $runs | Where-Object { $_.name -eq $name } | Sort-Object -Property id -Descending | Select-Object -First 1
        if (-not $latest -or $latest.status -ne 'completed') {
            $pending += $name
        }
        elseif ($latest.conclusion -ne 'success') {
            $failed += "$name ($($latest.conclusion))"
        }
    }

    if ($failed.Count -gt 0) {
        throw "CI が成功していないため公開しません ($Sha): $($failed -join ', ')"
    }

    if ($pending.Count -eq 0) {
        Write-Host "CI の必須ジョブはすべて成功しています ($Sha): $($RequiredChecks -join ', ')"
        return
    }

    if ((Get-Date) -ge $deadline) {
        throw "CI の完了を $TimeoutMinutes 分待ちましたが、未完了のジョブがあります ($Sha): $($pending -join ', ')"
    }

    Write-Host "CI の完了を待っています: $($pending -join ', ')"
    Start-Sleep -Seconds $PollSeconds
}
