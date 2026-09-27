---
id: register-while-stopped
required: true
---

# register-while-stopped: 購読停止中からのレビュー登録（#185）

購読を止めた状態で「レビューを手動開始」から PR を登録すると、確認ダイアログを経て購読が始まり、そのあとで `enqueue_review` が呼ばれることを確かめる。

## 前提

- 作業ツリーがクリーンで、HEAD がテスト対象のコミット
- `enqueue_review` は dummy subscriber（`call` モード）経由で fake Gateway にだけ届く。本番の queue には書き込まない

## 手順

1. 環境を起動し（isolated-launch の手順 1 と同じ。`-Scenario register-while-stopped`）、購読が始まるまで待つ（isolated-launch の手順 2 と同じ）
2. 購読を止める。停止は `StatusText` ではなくボタンの状態で判定する（停止後も `StatusText` は `Subscribed.` のまま）

   ```powershell
   pwsh -File $uia -ProcessId $run.appProcessId -EvidencePath $ev -Action Click -AutomationId StopButton
   pwsh -File $uia -ProcessId $run.appProcessId -EvidencePath $ev -Action WaitEnabled -AutomationId StartButton -TimeoutSeconds 30
   ```

3. PR を入力して「レビュー開始」を押す。reason は既定の `opened` のまま

   ```powershell
   pwsh -File $uia -ProcessId $run.appProcessId -EvidencePath $ev -Action SetText -AutomationId PrReferenceBox -Text 'fixture-owner/fixture-repository#320'
   pwsh -File $uia -ProcessId $run.appProcessId -EvidencePath $ev -Action Click -AutomationId EnqueueReviewButton
   ```

4. 確認ダイアログ（「購読が停止しています」）の「購読を開始して登録」を押す。ContentDialog の主ボタンの AutomationId は `PrimaryButton`

   ```powershell
   pwsh -File $uia -ProcessId $run.appProcessId -EvidencePath $ev -Action Click -AutomationId PrimaryButton -TimeoutSeconds 10
   ```

5. 購読が再開したことを確かめる

   ```powershell
   pwsh -File $uia -ProcessId $run.appProcessId -EvidencePath $ev -Action WaitEnabled -AutomationId StopButton -TimeoutSeconds 30
   ```

6. `enqueue_review` の記録を確かめる: `$run.subscriberObservation` のうち `"mode":"call"` の行
7. ログを確かめる: `$run.dataRoot/logs/winui3.log` で、`State: Running` の行が `Enqueuing review: fixture-owner/fixture-repository#320 (reason=opened)` より前にあり、続けて `enqueue_review call finished. ExitCode=0` がある
8. 後始末と漏れの検査（isolated-launch の手順 5 と同じ）

## 合否

すべて満たせば pass。

| 基準 | 証跡 |
|---|---|
| 手順 2 で `StartButton` が有効になる | `evidence/uia.jsonl`（kind: `uia`） |
| 手順 4 のダイアログの `PrimaryButton` が見つかり、押せる | `evidence/uia.jsonl`（kind: `uia`） |
| 手順 5 で `StopButton` が有効になる（購読中） | `evidence/uia.jsonl`（kind: `uia`） |
| 手順 6 の記録がちょうど 1 行で、`--tool enqueue_review`、`--url` が `$run.gatewayUrl`、`--args` が `{"owner":"fixture-owner","repo":"fixture-repository","prNumber":320,"reason":"opened"}`、`exitCode` が 0 | `observations/subscriber.jsonl`（kind: `observation`） |
| 手順 7 の順序で行がある | `data/logs/winui3.log`（kind: `log`） |
| `isolation.json` が `isolated: true` | `evidence/isolation.json`（kind: `isolation`） |
