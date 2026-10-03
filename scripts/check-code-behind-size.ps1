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
    # 監視対象選択、Recent activity のログ行保持、Recent review events の一覧保持・処理順序、レビュー登録結果の分類、Gateway URL / Resource URI 入力フロー、Gateway login lifecycle、実行ウィンドウ lifecycle、レビュー通知の表示経路、Settings 入力状態を Coordinator へ移した。WinUI 固有の表示アダプターは code-behind 側に残している。
    # #305 の resume 設定欄・切替・対応状況表示の UI 配線を反映。判断は Helpers 側に配置している。
    # #339 の実行終了・起動放棄イベントの購読・UI スレッドへの配送・保留分の起動結果の表示を反映。保留と再評価の判断は Services 側に配置している。
    # #340 の Auto-Pause 解除・再評価予約イベントの購読と、保留理由を通知へ渡す配線を反映。
    # #256 の「レビューに対応」表示トグルの配線を反映。表示状態は ViewModels 側に配置している。
    # #349 のログインダイアログ Closed 通知を反映。ユーザー閉鎖とプログラム閉鎖の判定は Services 側に配置している。
    # #257 の review cycle 状態変更購読と UI スレッド配送を反映。サイクル判定・永続化は Services 側に配置している。
    # #388 のトレイ通知初期化待ちを TrayNotificationCoordinator へ移し、code-behind の行数が減った。
    # #456 の CI 確定待ちのゲートの生成・再評価の契機の購読と破棄・通知への注記の受け渡しを反映。判断は Helpers / Services 側に配置している。
    # #452 のログ入力・レイアウト通知・末尾移動の UI 配線を反映。追従判断と件数は LogTailViewModel へ配置。
    "winui3/SquirrelNotifier.WinUI3/MainWindow.xaml.cs"              = 1500
    # #305 の resume 失敗メッセージを既存 InfoBar に表示する配線を反映。
    "winui3/SquirrelNotifier.WinUI3/AgentExecutionWindow.xaml.cs"    = 318
    # #276 の status client / Coordinator の生成と終了時破棄を反映。
    # #257 の ReviewCycleStore / Coordinator の composition root 配線を反映。
    # #427 の statusline サマリ書き出しの生成・起動時書き出し・終了時削除の配線を反映。
    # #462 の review-status 書き出し（公開契約）の生成・起動時書き出し・終了時削除・購読状態の変化の配線を反映。判断・状態・I/O は Services / Helpers 側に配置している。
    "winui3/SquirrelNotifier.WinUI3/App.xaml.cs"                     = 137
    # #340 の保留理由を通知の概要文へ渡す引数を反映。
    "winui3/SquirrelNotifier.WinUI3/ReviewNotificationPopup.xaml.cs" = 83
}

. (Join-Path $PSScriptRoot 'Get-CodeBehindSizeFindings.ps1')
$findings = Get-CodeBehindSizeFindings -RepositoryRoot $RepositoryRoot -Limits $limits
$violations = $findings.Violations
$missing = $findings.Missing
foreach ($measurement in $findings.Measurements) {
    $status = if ($measurement.Actual -gt $measurement.Limit) { "NG" } else { "ok" }
    Write-Host ("{0,-4} {1,6} / {2,-6} {3}" -f $status, $measurement.Actual, $measurement.Limit, $measurement.Path)
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
