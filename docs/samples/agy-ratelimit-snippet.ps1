# squirrel-notifier 連携: agy (Antigravity CLI) の statusline JSON からレートリミット
# 状態を共通スキーマに変換してローカルファイルへ書き出す（#139）。
#
# 使い方: 既存の statusline スクリプト（例: agy/statusline.ps1）で $data に
# stdin の JSON をパースした後、以下の関数呼び出しを追記する。
#   Write-SquirrelNotifierRateLimitStatus -Data $data

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
            $limits += [PSCustomObject]@{
                id      = "agy-$key"
                label   = $bucketLabels[$key]
                resetAt = $bucket.reset_time
            }
        }
    }

    $payload = @{ limits = $limits } | ConvertTo-Json -Depth 5
    $tempPath = Join-Path $outDir "agy.json.tmp"
    $finalPath = Join-Path $outDir "agy.json"
    Set-Content -Path $tempPath -Value $payload -Encoding utf8
    Move-Item -Path $tempPath -Destination $finalPath -Force
}
