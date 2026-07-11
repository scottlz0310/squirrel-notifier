# squirrel-notifier 連携: agy (Antigravity CLI) の statusline JSON からレートリミット
# 状態を共通スキーマ（schemaVersion 1、usedPercentage 対応）に変換してローカルファイルへ
# 書き出す（#139、#145）。
#
# 使い方: 既存の statusline スクリプト（例: agy/statusline.ps1）で $data に
# stdin の JSON をパースした後、以下の関数呼び出しを追記する。
#   Write-SquirrelNotifierRateLimitStatus -Data $data
#
# 注意: statusline はインタラクティブセッションの表示機構であり、ヘッドレス実行では
# 発火しない。そのためヘッドレス実行の前後で squirrel-notifier が Delta（レビュー
# サイクル単位の使用率差分）を算出できるとは限らない。Delta は best-effort であり、
# 「取得不可」は正常系として扱われる（詳細は ../statusline-integration.md を参照）。

function Write-SquirrelNotifierRateLimitStatus {
    param($Data)

    if (-not $Data -or -not $Data.quota) {
        return
    }

    $outDir = Join-Path $env:LOCALAPPDATA "SquirrelNotifier\ratelimit-status"
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null

    $bucketLabels = [ordered]@{
        "3p-5h"         = "agy 3rd-party 5時間枠"
        "3p-weekly"     = "agy 3rd-party 週次枠"
        "gemini-5h"     = "agy Gemini 5時間枠"
        "gemini-weekly" = "agy Gemini 週次枠"
    }

    $limits = @()
    foreach ($key in $bucketLabels.Keys) {
        $bucket = $Data.quota.$key
        if ($bucket -and $bucket.reset_time) {
            # agy は remaining_fraction（残量割合 0〜1）を持つため、
            # squirrel-notifier の usedPercentage（使用率 0〜100）へ正規化する
            $usedPercentage = $null
            if ($null -ne $bucket.remaining_fraction) {
                $usedPercentage = [Math]::Round((1 - $bucket.remaining_fraction) * 100, 2)
            }

            $limits += [PSCustomObject]@{
                id             = "agy-$key"
                label          = $bucketLabels[$key]
                resetAt        = $bucket.reset_time
                usedPercentage = $usedPercentage
            }
        }
    }

    $observedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    $payload = @{
        schemaVersion = 1
        agentId       = "agy"
        observedAt    = $observedAt
        limits        = $limits
    } | ConvertTo-Json -Depth 5

    $tempPath = Join-Path $outDir "agy.json.tmp"
    $finalPath = Join-Path $outDir "agy.json"
    Set-Content -Path $tempPath -Value $payload -Encoding utf8
    Move-Item -Path $tempPath -Destination $finalPath -Force
}
