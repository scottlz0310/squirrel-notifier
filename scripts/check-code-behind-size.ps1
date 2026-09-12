<#
.SYNOPSIS
  XAML code-behind（*.xaml.cs）の行数が上限を超えていないか検証する（#263）。
.DESCRIPTION
  *.xaml.cs はカバレッジ計測から除外されているため（Tests.csproj の ExcludeByFile /
  codecov.yml の ignore）、ここへロジックを書くとテストを書かずにゲートを通せてしまう。
  肥大化を機械的に検知するため、ファイルごとに行数の上限（ratchet）を設ける。

  上限は「現在の実測値」に設定する。抽出が進んで行数が減ったら、その PR で上限も下げる
  （ratchet を締める）。逆に上限を超える追加が必要な場合は、同じ PR で上限を引き上げる。
  上限の引き上げ自体は禁止しないが、無意識に増やせないようにするのがこのチェックの目的である。

  epic #262 の完了時点で実測値に余裕を足した最終値へ確定させる。
.EXAMPLE
  pwsh -File ./scripts/check-code-behind-size.ps1
#>
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"

# ファイルごとの行数上限（リポジトリルートからの相対パス）。
# 値の更新は「抽出して減った」「意図して増やす」のいずれかを PR で説明できるときだけ行う。
$limits = [ordered]@{
    # #286 の URL・ログフォルダー起動・ウィンドウアイコン・Clipboard・購読状態表示・トレイコマンド・終了処理・通知予約切替・監視対象選択の抽出を反映。
    # 監視対象選択、Recent activity のログ行保持、Recent review events の一覧保持を Coordinator へ移したため、実測値を 1509 行へ更新する。
    "winui3/SquirrelNotifier.WinUI3/MainWindow.xaml.cs"              = 1509
    "winui3/SquirrelNotifier.WinUI3/AgentExecutionWindow.xaml.cs"    = 317
    # #276 の status client / Coordinator の生成と終了時破棄を反映。
    "winui3/SquirrelNotifier.WinUI3/App.xaml.cs"                     = 109
    "winui3/SquirrelNotifier.WinUI3/ReviewNotificationPopup.xaml.cs" = 82
}

$violations = New-Object System.Collections.Generic.List[string]
$missing = New-Object System.Collections.Generic.List[string]

# 上限が未登録の *.xaml.cs を検出する。新しい code-behind を追加したまま
# チェック対象から漏れることを防ぐ
$tracked = $limits.Keys | ForEach-Object { $_.ToLowerInvariant() }
$found = Get-ChildItem -Path (Join-Path $RepositoryRoot "winui3") -Filter "*.xaml.cs" -Recurse -File |
    Where-Object { $_.FullName -notmatch "[\\/](obj|bin)[\\/]" }

foreach ($file in $found) {
    $relative = [System.IO.Path]::GetRelativePath($RepositoryRoot, $file.FullName).Replace("\", "/")
    if (-not ($tracked -contains $relative.ToLowerInvariant())) {
        $missing.Add($relative)
    }
}

foreach ($entry in $limits.GetEnumerator()) {
    $relative = $entry.Key
    $limit = $entry.Value
    $path = Join-Path $RepositoryRoot $relative
    if (-not (Test-Path -LiteralPath $path)) {
        $violations.Add("$relative : 上限が登録されているファイルが存在しません（削除したなら limits からも消してください）")
        continue
    }

    # 空行を含む物理行数で数える。Measure-Object -Line は空行を数えないため、
    # エディタや wc -l が示す行数と食い違い、上限の意味が分かりにくくなる
    $actual = @(Get-Content -LiteralPath $path).Count
    $status = if ($actual -gt $limit) { "NG" } else { "ok" }
    Write-Host ("{0,-4} {1,6} / {2,-6} {3}" -f $status, $actual, $limit, $relative)

    if ($actual -gt $limit) {
        $violations.Add("$relative : $actual 行（上限 $limit 行、超過 $($actual - $limit) 行）")
    }
}

if ($missing.Count -gt 0) {
    Write-Host ""
    Write-Host "上限が未登録の code-behind があります:" -ForegroundColor Red
    $missing | ForEach-Object { Write-Host "  - $_" }
    Write-Host "scripts/check-code-behind-size.ps1 の `$limits へ追加してください。"
}

if ($violations.Count -gt 0) {
    Write-Host ""
    Write-Host "行数の上限を超えた code-behind があります:" -ForegroundColor Red
    $violations | ForEach-Object { Write-Host "  - $_" }
    Write-Host ""
    Write-Host "判断・状態・I/O は Helpers/ または Services/ へ出してください（AGENTS.md の「コードの置き場所」参照）。"
    Write-Host "意図した増加であれば、同じ PR で `$limits の値を更新し、理由を PR に記載してください。"
}

if ($violations.Count -gt 0 -or $missing.Count -gt 0) {
    exit 1
}

Write-Host ""
Write-Host "OK: すべての code-behind が上限内です。"
