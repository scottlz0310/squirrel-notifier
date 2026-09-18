# 全自動レビューサイクルの運用

PR レビューの開始・通知・修正・再レビューを、人のクリックなしで回すための運用手順。**マージの判断だけは人が行う**。

Squirrel Notifier 単体の設定は README の「Settings」節に、Auto-Pause の判定と解除は
[auto-pause.md](auto-pause.md) にある。本書はそれらを**サイクル全体の中でどう組み合わせるか**に絞る。

## 起動モードによる分岐

**手順は thread-owl の起動モードで変わる。先に確認すること。**
compose 定義の `thread-owl` サービスの `command` を見る（`docker compose config`）。
queue の観測結果から推測してはならない。webhook はリトライを伴う非同期配送のため、
ある時点で queue に載っていないことは `--mcp-http` である証拠にならない。

| 起動モード | queue への入口 | 実装 CLI の `enqueue_review` | 完了待機 |
|---|---|---|---|
| `--mcp-http`（Mcp-Docker の既定） | 明示 `enqueue_review` のみ | **必須** | できる |
| `--webhook-mcp-http` | `POST /webhook` からの自動 enqueue | **行わない** | できない |

`--webhook-mcp-http` で明示 `enqueue_review` を重ねてはならない。queue の中身は PR キーで
dedup されるが、**enqueue のたびに購読者への通知 listener が発火する**ため、`notifications/
resources/updated` が二重に飛び、自動起動が有効な環境では reviewer が二重起動し得る。
webhook の delivery-id による重複排除は webhook 受信経路にしか効かない。

また `review://status` を `pending` にリセットするのは `enqueue_review` tool だけで、webhook
経由の enqueue ではリセットされない。そのため `--webhook-mcp-http` では完了待機の前提が
成り立たず、待機せずにコメントの投稿をもってサイクルを進める。

以降は **`--mcp-http` を前提**に書く。

## 全体像（`--mcp-http`）

```text
1. 実装 CLI エージェントが PR を作成・更新し、enqueue_review を呼ぶ
     → thread-owl の review://status/{owner}/{repo}/{prNumber} が pending にリセットされ、queue に載る
2. 実装 CLI エージェントが mcp-resource-subscriber で同じ Resource を購読し、
     同一セッションのままレビュー完了を待つ
3. Squirrel Notifier が queue event を受けて reviewer を自動起動する
4. reviewer がサマリー / Verdict を投稿する
     → status が reviewed / approved になり、2 の待機が解ける
5. 実装 CLI エージェントが未解決スレッドを取得し、
     - 指摘あり: 修正・返信・resolve → re-review-requested で enqueue して 2 へ戻る
     - 指摘なし: 人にマージ判断を仰いで終了
```

このモードでは `enqueue_review` を呼ばない限り queue には何も載らない。**コメントの
投稿だけではサイクルは始まらない**。

## 責務の分担

| 担当 | 役割 | 持たないもの |
|---|---|---|
| 実装 CLI エージェント | PR の作成・更新、`enqueue_review`、完了の待機、指摘への対応 | reviewer の起動 |
| mcp-resource-subscriber | subscribe / read / timeout といった通信 | レビュー業務の状態 |
| Squirrel Notifier | reviewer の起動、通知、ローカル UI、子プロセスのライフサイクル | レビュー業務状態の正本、実装者を起こす役割 |
| thread-owl / GitHub | レビュー業務状態の正本（スレッド・コミット・`review://status`） | — |

Squirrel Notifier が自動化するのは **reviewer の起動だけ**である。実装者側を
コールドスタートしない（指摘への対応は、設計意図がコンテキストに残っている実装
セッションが引き受けるほうが良いため）。

## 前提

| 項目 | 値 | 確認方法 |
|---|---|---|
| Settings →「レビュー自動開始」 | 有効 | 既定は無効。opt-in |
| ユーザー環境変数 `MCP_GATEWAY_PUBLIC_URL` | gateway の公開 URL | 待機側が `<URL>/mcp/thread-owl` を組み立てる |
| reviewer スロットのプリセット | レートリミットを取得できる CLI | Auto-Pause の対象になる（[auto-pause.md](auto-pause.md)） |
| Settings →「セッション resume」 | 任意 | 有効にすると同一 PR の 2 周目が前回の会話を引き継ぐ |

`MCP_PROBE_URL` は設定しない。thread-owl の route を含む完全 URL で、subscriber の既定
`--url` にもなるため、他の route を購読するときに邪魔になる。

## 自動起動の判定順序

queue event を受け取ると、Squirrel Notifier は次の順に判定する。どこで見送っても
理由を「Recent activity」に記録する。

1. 「レビュー自動開始」が無効 → 起動しない（記録も残さない。無効時の挙動を変えないため）
2. reviewer 側のアクションを伴わない reason → 起動しない
3. PR がマージ・クローズ済み → 起動しない
4. **別のレビューが実行中、または起動処理中** → **保留**し、実行が終わった時点で再評価する
5. **Auto-Pause が Paused** → **保留**し、解除を確認した時点で再評価する
6. いずれにも当たらない → 起動する

### 保留と再開

4 と 5 で保留したイベントは PR 単位で 1 件だけ保持され、受信順を保つ。同じ PR の
イベントが後から届いた場合は、順番を保ったまま新しいほうへ差し替える。

再評価の契機は次のとおり。

| 保留の理由 | 再評価の契機 |
|---|---|
| 別のレビューが実行中 | 実行の終了。起動処理がレビューを起動せずに終わった場合はその時点 |
| Auto-Pause | 解除の確認（レートリミット欄の「更新」、レビューの終了時 snapshot）、およびリセット時刻の通過。そこで解除を確認できない場合は 15 分ごと |

再評価でも上の 1〜6 を通すため、保留中に設定が変わった・PR が閉じた・一覧から消えた
場合は起動しない。保留はメモリ上にのみ持ち、アプリを再起動すると失われる（イベントは
一覧に残るので手動で起動できる）。

保留したことはトレイ通知にも「レビューを保留中（理由）」として出る。無人運用では
通知が唯一の気づく手段になるため、自動起動しなかっただけのイベントとは文言を分けている。

## reason の使い分け

`enqueue_review` の `reason` は 3 値で、reviewer skill の起動モード（`initial-review` /
`re-review`）とは別物である。

| reason | 使うとき |
|---|---|
| `opened` | PR を新規作成した直後 |
| `synchronized` | 既存 PR へ push した直後 |
| `re-review-requested` | 指摘に対応し終えて再レビューを求めるとき |

## 止まったときの確認

待機が `--timeout-ms` に達した場合、原因は待機側からは分からない。Squirrel Notifier の
「Recent activity」を見ると、次のどれかが分かる。

- `のレビューを保留しました: 別のレビューが実行中のため` → 先行するレビューの終了待ち
- `のレビューを保留しました: Auto-Pause 中のため` → 解除待ち。リセット時刻も同じ行に出る
- `のレビューを自動起動しませんでした:` → reason による見送り
- **どの行も無い** → 「レビュー自動開始」が有効なら、queue event が届いていない可能性が高い。
  `enqueue_review` の呼び出しと、購読（`queue://review/queue`）の状態を確認する

最後の項目は、先に **「レビュー自動開始」の設定を確認してから**判断する。無効なときは
イベントを受信しても記録を残さない（上の判定順序の 1）ため、ログの欠落だけでは
「queue event が届いていない」と「設定が無効」を区別できない。設定が有効であることを
確かめたうえで、なお記録が無い場合に queue 側を疑う。

解除までの時間が待機のタイムアウトを超えると、実装者側は先にタイムアウトする。その後
reviewer が起動してサマリーが投稿されても実装者側は自動では再開しないため、同じ
セッションで待機からやり直す。

## Verdict と CI の確認

reviewer が `Status: READY_TO_MERGE` の Verdict を投稿しても、**その時点で CI が完了して
いるとは限らない**。実測では、7 件の check のうち 5 件（`build-and-test` を含む）が未完了の
まま Verdict が投稿された例がある。

したがって reviewed 側は、Verdict の有無や内容に関わらず、**自分で check runs を取得して
確認してから**マージ判断へ進む。手順は次のとおり。

1. PR の現在の HEAD SHA を取得し、`reviewedHeadSha` として固定する
2. **その SHA に対して** check runs を取得する。PR 番号を入力とする経路は、応答が対象 SHA を
   含まないため「どのコミットに対する結果か」を照合できない。使わない
3. 各 run の `head_sha` が `reviewedHeadSha` と一致することと、全ページ取得が完了したことを
   確認する
4. **マージ判断の直前に PR の HEAD をもう一度取得し、`reviewedHeadSha` と一致することを
   確認する**

4 を省いてはならない。check runs を取得した後に push が入ると、古い HEAD の結果を根拠に
新しい HEAD をマージしてしまう。不一致なら取得済みの結果を破棄し、新しい SHA で 1 からやり直す。

### required と optional を分ける

「全件 success」を機械的に適用すると、品質ゲート外の失敗で不要に停止する。**required checks が
`completed` かつ `success`** であることをマージ条件とし、optional checks の結果は記録に留める。

required かどうかは check runs の応答に含まれないため、リポジトリの方針（branch protection、
workflow の定義）から別途判断する。本リポジトリでは `headless-e2e` が `.github/workflows/ci.yml`
に non-required と明記されており（#307 の初期導入時の判断）、この job の失敗だけを理由に
マージ判断を止めることはしない。失敗した場合は記録して原因を追う。

check が未返却でも、その workflow が path フィルタで対象外なら失敗ではない。たとえば
Changelog Guard の `verify` は csproj・CHANGELOG・当該 workflow の変更でしか起動しないため、
docs のみの PR では実行されない。

## 関連

- [auto-pause.md](auto-pause.md): Auto-Pause の判定対象・解除条件・トラブルシューティング
- [launcher-agent-presets.md](launcher-agent-presets.md): プリセットごとの起動引数と resume の仕様
- [statusline-integration.md](statusline-integration.md): レートリミット snapshot の生成
