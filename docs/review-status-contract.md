# レビュー状態の公開契約: `review-status.json`（#462）

Squirrel Notifier は、reviewer の**起動待ち・実行中・終了**の状態と、その**時刻・保留の理由**を、外部のプログラム（レビュー対応の skill、statusline など）が読めるよう、ファイルへ書き出す。レビュー対応の待機を「時間」ではなく「状態」で打ち切れるようにするための契約である。

```
%LOCALAPPDATA%\SquirrelNotifier\review-status.json
```

## 3 つのファイルの違い

| ファイル | 性質 | 読むのは | 内容 |
|---|---|---|---|
| `review-status.json`（このページ） | **公開契約**（`schemaVersion: 1`） | skill、スクリプトなど | 起動待ち・実行中・終了の PR と、時刻・保留の理由・終了コード。**鮮度を `updatedAt` で判定できる** |
| `statusline-summary.json`（[#427](statusline-integration.md#逆方向レビューキュー状態のサマリ427)） | 公開契約（`schemaVersion: 1`） | statusline（agent-statusline） | 起動待ちと実行中の PR の最小限のサマリ。頻繁に読まれるため、小さく安定したまま保つ |
| `review-cycles.json` | **内部ストア**。契約ではない | Squirrel Notifier 自身 | PR ごとのサイクル状態。構造は予告なく変わる。**外部は読まない** |

## 位置づけ

- 内容は、Squirrel Notifier が**観測した実行状態の、読み取り専用の写し**である。レビューの結果の正本ではない。**レビューが完了したかどうかは、thread-owl の `review://status/<owner>/<repo>/<pr>`（`status` と `headSha`）で判定する**。
- `outcome` / `exitCode` は **reviewer のプロセスの終了結果**であり、レビューの結果（Verdict）ではない。`completed` でも、Verdict を投稿せずに終わっている場合がある。
- ファイルが無いことは、Squirrel Notifier が**起動していない**ことを表す（起動時に空の文書を書き出し、終了時に削除する）。
- パス、コマンドライン、トークン、ログ本文は含まない。

## 鮮度（アプリの異常終了への備え）

`updatedAt` は、状態が変わったときと、**60 秒ごと**に更新する（heartbeat）。異常終了すると、最後の文書が残る。**`updatedAt` が現在時刻より 3 分以上古い場合は、観測できない（Squirrel Notifier が止まっている）ものとして扱う**こと。

## スキーマ（schemaVersion 1）

```json
{
  "schemaVersion": 1,
  "updatedAt": "2026-10-01T02:30:00Z",
  "app": { "version": "0.15.0", "startedAt": "2026-10-01T00:00:00Z" },
  "concurrency": { "active": 1, "max": 1 },
  "subscription": { "state": "running", "since": "2026-10-01T00:00:05Z" },
  "items": [
    {
      "key": "scottlz0310/mcp-docker#340",
      "repository": "scottlz0310/Mcp-Docker",
      "prNumber": 340,
      "round": 1,
      "eventId": "evt_scottlz0310_Mcp-Docker_340_opened_2026-10-01T01_08_13.119Z",
      "reason": "opened",
      "state": "running",
      "receivedAt": "2026-10-01T01:08:13Z",
      "startedAt": "2026-10-01T01:08:14Z",
      "agent": "codex"
    },
    {
      "key": "scottlz0310/thread-owl#245",
      "repository": "scottlz0310/thread-owl",
      "prNumber": 245,
      "round": 1,
      "eventId": "evt_scottlz0310_thread-owl_245_opened_2026-10-01T01_09_00.000Z",
      "reason": "opened",
      "state": "waiting",
      "receivedAt": "2026-10-01T01:09:00Z",
      "holdReason": "busy",
      "holdSince": "2026-10-01T01:09:00Z",
      "queuePosition": 1
    }
  ],
  "recent": [
    {
      "key": "scottlz0310/mcp-docker#339",
      "repository": "scottlz0310/Mcp-Docker",
      "prNumber": 339,
      "round": 2,
      "eventId": "evt_scottlz0310_Mcp-Docker_339_re-review-requested_2026-10-01T00_55_40.660Z",
      "reason": "re-review-requested",
      "state": "finished",
      "receivedAt": "2026-10-01T00:55:40Z",
      "startedAt": "2026-10-01T00:55:41Z",
      "finishedAt": "2026-10-01T01:02:23Z",
      "agent": "codex",
      "outcome": "completed",
      "exitCode": 0
    }
  ]
}
```

### 全体

| フィールド | 説明 |
|---|---|
| `schemaVersion` | `1` 固定。互換性のない変更をするときに上げる |
| `updatedAt` | 書き出し時刻（UTC、ISO 8601）。60 秒ごとに更新する |
| `app.version` / `app.startedAt` | Squirrel Notifier の版と、起動時刻 |
| `concurrency.active` / `concurrency.max` | 実行中の reviewer の件数と、同時に実行できる件数（現在は 1。並列実行は未対応） |
| `subscription.state` | thread-owl への購読の状態。`running` / `starting` / `stopping` / `stopped` / `error` / `authRequired`（認証が要る）。`subscription.since` は、その状態になった時刻 |

### `items[]`（起動待ちと実行中）、`recent[]`（終了）

`items[]` は、実行中、起動待ち（保留し始めた順）の並び。`recent[]` は、終了の新しい順で、**直近 24 時間かつ最大 50 件**。同じ PR が `items[]` と `recent[]` の両方に現れてよい（前のラウンドが終了し、次のラウンドが待っている場合など）。

**同じ PR の実行中に、次の event（再レビューの依頼など）が届いた場合**、`items[]` の実行中の項目は変わらない（`eventId`・`reason`・`receivedAt`・`startedAt` は、実行中の event のまま）。その実行が終わると、実行中の項目が `recent[]` へ移り、次の event が `waiting` の項目として現れる。実行中に保留（`busy`）を観測していれば、その `holdReason`・`holdSince`・`queuePosition` を引き継ぐ。

| フィールド | 説明 |
|---|---|
| `key` | **小文字**の `owner/repo#N`。thread-owl の `review://status/<owner>/<repo>/<pr>` の URI と同じ表記で、照合に使う。`repository` は元の大文字小文字のまま（表示用） |
| `round` | レビューのラウンド（再レビューの依頼で増える） |
| `eventId` / `reason` | この回の queue event の ID と reason（`opened` / `synchronized` / `re-review-requested`） |
| `state` | `waiting`（起動待ち）、`running`（実行中）、`finished`（終了。`recent[]` のみ） |
| `receivedAt` | queue event を受信した時刻 |
| `startedAt` | reviewer を起動した時刻（`running` と `finished`） |
| `finishedAt` | reviewer のプロセスが終了した時刻（`finished`） |
| `agent` | reviewer スロットのプリセット ID（`claude` / `codex` / `agy` / `custom` など）。取得できない場合は省略 |
| `holdReason` | `waiting` のときだけ。起動を待っている理由（下表）。評価の途中など、理由が決まっていない場合は省略 |
| `holdSince` | `holdReason` があるときだけ。待ち始めた時刻 |
| `queuePosition` | 保留（`busy` / `ciPending` / `autoPause`）を観測しているときだけ。保留した順の待ち順（1 から） |
| `outcome` | `finished` のとき。`completed`（プロセスが正常に終了）または `failed`。**Verdict ではない** |
| `exitCode` | `finished` のとき。プロセスの終了コード。取得できない場合は省略 |

**`holdReason`**

| 値 | 意味 | 解除の契機 |
|---|---|---|
| `busy` | 別のレビューを実行中のため、実行終了後の再評価まで保留している | 実行中のレビューの終了 |
| `ciPending` | required checks が確定していないため、確定または上限（12 分）まで保留している（#456） | CI の確定、または上限 |
| `autoPause` | Auto-Pause 中のため、解除後の再評価まで保留している（#340） | Auto-Pause の解除 |
| `manual` | 「レビュー自動開始」が off（または手動運用）で、「レビューする」の操作を待っている | **利用者の操作**（自動では解除されない） |

## 消費者の判定の例（reviewed 側の待機）

`review://status` が `pending` のまま、購読がタイムアウトしたとき、Squirrel Notifier の状態で、待ち続けるか打ち切るかを決める。**完了の判定には使わない**（完了は常に `review://status` で判定する）。

| `key` に対応する状態 | 判断 |
|---|---|
| `items[]` に `waiting` で、`holdReason` が `busy` / `ciPending` / `autoPause` | 待ち続ける（購読を起動し直す。`enqueue_review` はやり直さない） |
| `items[]` に `waiting` で、`holdReason` が `manual` | 打ち切る。「レビューする」の操作を案内する |
| `items[]` に `running` | 待ち続ける |
| `recent[]` に `finished`（`receivedAt` が今回の `enqueue_review` より後） | 打ち切る。Verdict なしで終了した。`exitCode` を報告する |
| どこにもない（`enqueue_review` から 2 分を超えた） | 打ち切る。Squirrel Notifier が受信していない |
| ファイルが無い、または `updatedAt` が 3 分より古い | 観測できない |

打ち切る前に、`review://status` の現在値をもう一度取得すること（判定から打ち切りまでの間に完了した場合を拾うため）。

## 互換性

- **省略可能なフィールドの追加は、`schemaVersion` を上げない。** 消費者は、知らないフィールドを無視すること。
- 互換性のない変更（フィールドの削除、意味の変更）をするときだけ、`schemaVersion` を上げる。
- `items[]` と `recent[]` は **N 件**を前提にする。消費者は、実行中の件数が 1 であることを前提にしないこと（将来の並列実行に備える）。
- `statusline-summary.json` は変えない。

## 書き出しの仕様

- 状態が変わるたびに、全体を書き直す。`.tmp` に書いてから置換するため、読み取り側が不完全な JSON を読むことはない。
- 読み取り側は `FileShare.Delete` を付けずに開いていても、置換は短い間隔で数回再試行する。読み取りは開いてすぐ閉じること。
- マージ・クローズ済みの PR の自動削除（Recent review events の整理）で、その PR の項目を落とす。
