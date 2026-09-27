---
id: review-event-flow
required: true
---

# review-event-flow: イベントの受信、通知ポップアップ、reviewer の起動

queue のイベントを受信すると、通知ポップアップと Recent review events に表示され、「レビューする」で reviewer が起動し、レビューサイクルの状態が更新されることを確かめる。

## 前提

- 作業ツリーがクリーンで、HEAD がテスト対象のコミット
- dummy subscriber は PR #307（opened）と #308（synchronized）を返し、続けて #309（re-review-requested）を加える

## 手順

1. 環境を起動し、**同じコマンドの中で続けて**手順 2 の読み取りまで行う（isolated-launch の手順 1 と同じ変数を用意する。`-Scenario review-event-flow`）。通知ポップアップはフォーカスを失うと閉じるため、起動から読み取りまでの間に、エージェント自身の画面を含め前面のウィンドウを操作・更新しない
2. 通知ポップアップを読む。3 件のイベントは同時に届いて並行に処理されるため、どの 1 件が表示されるかは決まっていない。表示は、reason と PR の正しい組のどれかに完全一致しなければならない

   ```powershell
   pwsh -File $uia -ProcessId $run.appProcessId -EvidencePath $ev -Action WaitText -AutomationId TitleText -TimeoutSeconds 60 -Text '^(opened: fixture-owner/fixture-repository#307|synchronized: second-owner/second-repository#308|re-review-requested: third-owner/third-repository#309)$'
   ```

   見つからずに失敗した場合、`evidence/uia.jsonl` の最後の行に、そのときの前面のウィンドウ（`foregroundWindow`）と、アプリのウィンドウ一覧（`processWindows`）が残る。結果の `flaky` に書き写す

3. Recent review events に 3 件あることを確かめる

   ```powershell
   pwsh -File $uia -ProcessId $run.appProcessId -EvidencePath $ev -Action WaitText -AutomationId ReviewEventList -Text 'fixture-repository #307' -TimeoutSeconds 30
   pwsh -File $uia -ProcessId $run.appProcessId -EvidencePath $ev -Action Read -AutomationId ReviewEventList
   ```

4. #307 の「レビューする」を押す。ライブログウィンドウが開き、dummy launcher の終了後に自動で閉じる

   ```powershell
   pwsh -File $uia -ProcessId $run.appProcessId -EvidencePath $ev -Action Click -AutomationId LaunchReviewerButton -ListId ReviewEventList -ItemText 'fixture-repository #307'
   ```

5. dummy launcher の記録を確かめる: `$run.launcherObservation` の最後の行
6. #307 の状態が更新されたことを確かめる

   ```powershell
   pwsh -File $uia -ProcessId $run.appProcessId -EvidencePath $ev -Action WaitText -AutomationId ReviewEventList -Text 'fixture-repository #307 .*reviewer 実行完了' -TimeoutSeconds 30
   ```

7. ログを確かめる: `$run.dataRoot/logs/winui3.log` に `Review process finished. ExitCode=0` と `[Cycle] fixture-owner/fixture-repository #307 ラウンド 1 の reviewer 実行が終了しました` がある
8. 後始末と漏れの検査（isolated-launch の手順 5 と同じ）

## 合否

すべて満たせば pass。

| 基準 | 証跡 |
|---|---|
| 手順 2 のポップアップの `TitleText` が、`opened: fixture-owner/fixture-repository#307` / `synchronized: second-owner/second-repository#308` / `re-review-requested: third-owner/third-repository#309` のいずれかに完全一致する | `evidence/uia.jsonl`（kind: `uia`） |
| 手順 3 の `items` に #307 / #308 / #309 の 3 件がある | `evidence/uia.jsonl`（kind: `uia`） |
| 手順 5 の記録の `arguments` が `["review", "fixture-owner/fixture-repository#307", "opened"]`、`exitCode` が 0、`workingDirectory` が `$run.dataRoot` 配下の `launcher-workspace\reviewer\fixture-owner\fixture-repository\307` | `observations/launcher.jsonl`（kind: `observation`） |
| 手順 6 の文字列が一致する | `evidence/uia.jsonl`（kind: `uia`） |
| 手順 7 の 2 行がある | `data/logs/winui3.log`（kind: `log`） |
| `isolation.json` が `isolated: true` | `evidence/isolation.json`（kind: `isolation`） |
