# review-raven への観測の送信（#497 / S1）

Squirrel Notifier は、reviewer の**起動待ち・実行中・終了**の観測を、review-raven（依頼側の窓口）へ送れる。実装側のセッションが、Notifier ホストのファイルや画面に触れずに、レビューの途中経過（CI 待ち → 起動待ち → 実行中 → 結果）をリモートから購読できるようにするための、送信元である。

> **凍結中（2026-10-25）。有効にしない。** 送信の認証に使うサービス主体の形が未決のため（[内部連携の運用](https://github.com/scottlz0310/Mcp-Docker/blob/main/docs/review-internal-link.md)）。レビューの進捗は、この送信ではなく、公開状態（`review-status.json`、[契約](review-status-contract.md)）を `resource-bridge-cli watch-review-status`（v0.8.0 以降）で読む方式に倒した。この送信の実装は、必要になるかの判断がつくまで残している。

**既定は無効**。有効にしなければ、これまでと動作は変わらない。

## 位置づけ

- Squirrel Notifier は、**観測元**である。観測は、レビューの結果の正本ではない。レビューが完了したかどうかは、これまでどおり thread-owl の `review://status/<owner>/<repo>/<pr>`（`status` と `headSha`）と、GitHub 上の thread-owl の投稿で判定する。
- 送るのは、**review-raven 経由の候補**（依頼 cycle との対応 `cycleId` / `candidateId` / `headSha` を持つ項目）だけ。旧経路（`enqueue_review`・webhook）の候補は送らない。
- 契約は review-raven の `docs/design/contracts`（`publish_review_observation`）。公開状態（`review-status.json`、[契約](review-status-contract.md)）とは別の契約で、そちらの形式は変えない。相関の ID（`cycleId` など）は `review-status.json` に載せない。
- reviewer の選択変更・外部からの起動 API は追加しない。

## 有効にする

review-raven 側の前提（Mcp-Docker の [内部連携の運用](https://github.com/scottlz0310/Mcp-Docker/blob/main/docs/review-internal-link.md)）が済んでいること。

1. `%LocalAppData%\SquirrelNotifier\settings.json` に設定する。

   | 設定 | 内容 |
   |---|---|
   | `ObservationPublishEnabled` | `true` で送信する。既定は `false` |
   | `ObservationPublishUrl` | review-raven の内部面の URL（gateway の内部ルート。例: `https://<gateway>/mcp/review-raven-internal`）。空なら送信しない |
   | `ObservationProducerId` | 送信主体の ID。既定は `squirrel-notifier`。review-raven の保護設定 `REVIEW_RAVEN_SERVICE_ACL` の、この主体の `producers` に載せた ID と一致させる（小文字の英数字と `._-`、64 文字以内） |

2. サービス主体のトークンを、環境変数 **`SQUIRREL_OBSERVATION_TOKEN`** で渡す。
   - 内部面は、利用者ではなく**サービス主体**のトークンで呼ぶ（主体の形は未決。上の注記と Mcp-Docker の手順書を参照）。resource-bridge-cli の認証キャッシュは origin 単位で、同じ gateway の利用者のトークンと衝突するため、このトークンはキャッシュに入れない。
   - アプリは、トークンを**設定ファイルに保存せず、ログにも出さない**。resource-bridge-cli を起動するとき、子プロセスの `MCP_PROBE_AUTH_TOKEN` にだけ渡す（コマンドライン引数には載せない）。
   - トークンの発行・rotation・失効の手順は、Mcp-Docker の手順書を参照する。
3. アプリを再起動する（環境変数は起動時に決まる）。`ObservationPublishEnabled` / `Url` / `ProducerId` は、実行中に読み直す。

## 何を送るか

状態が変わったときと、**60 秒ごと**（`review-status.json` の heartbeat と同じ時刻）に、PR ごとに 1 件を送る。

| 項目 | 内容 |
|---|---|
| `cycleId` / `candidateId` / `headSha` | queue の候補に付いていた依頼 cycle との対応 |
| `producerId` / `producerEpoch` / `sequence` | 送信主体の ID、起動ごとに増える世代、cycle ごとに増える連番 |
| `observedAt` | 観測した時刻（UTC） |
| `state` | `waiting`（起動待ち）/ `running` / `finished` |
| `holdReason` | 起動待ちの理由（`ciPending` / `busy` / `manual` / `autoPause`）。公開状態と同じ規則 |
| `outcome` / `exitCode` | 終了のときだけ。reviewer の**プロセスの終了結果**で、Verdict ではない |
| `stopped` | アプリの終了を知らせる |

パス、コマンドライン、トークン、ログ本文、reviewer のプリセット（agent）は送らない。

- 起動待ち・実行中は、状態が同じでも heartbeat で送って鮮度を保つ。終了は、受理されたら二度と送らない。
- `producerEpoch` は、起動のたびに前回より大きくする（前回 + 1 と現在の UNIX 秒の大きいほう。`observation-producer.json` に保存）。review-raven は、より大きい世代を最初に受けた観測で新しい世代を登録し、旧世代を失効させる。
- アプリを終了するときは、起動待ち・実行中の項目に `stopped=true` を送る（最大 5 秒。終了は遅らせない）。

## 失敗したとき

送信の失敗は、**reviewer の起動、公開状態（`review-status.json`）、アプリの動作を止めない**。

| 失敗 | 扱い |
|---|---|
| 通信エラー、タイムアウト、認証の失敗（トークンの誤り・失効）、トークンまたは URL が未設定 | 送り直せる失敗。次の変化または heartbeat のときに、**そのときの最新の状態**を送り直す（古い状態は送らない）。同じ失敗の繰り返しはログに残さない |
| review-raven が拒否（割当外の producer、HEAD の不一致、旧世代、取消済みなど。契約の `retryable: false`） | その cycle の送信をやめる（他の cycle は続ける）。ログに code を残す |
| ツール名・引数・プロトコルの不一致 | 送り直しても成功しないため、その cycle の送信をやめる |

ログ（`winui3.log`）の `[Observation]` で原因を確認できる。

## 確認できていないこと

実機（実際の thread-owl・review-raven・gateway を経由した送信）では、まだ確認していない。次を、有効にしたあとに確認する。

- review-raven の `get_review_request`（または `review-raven://requests/{cycleId}` の購読）に、起動待ち → 実行中 → 終了が現れること。
- アプリを終了すると、`stopped` が反映されること。
- トークンが誤っているとき、ログに `OBSERVATION_AUTH_FAILED` が 1 回だけ出て、reviewer が通常どおり起動すること。
