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

## Recent review events のサイクル表示

Squirrel Notifier は reviewer 側の queue event とローカル reviewer プロセスのライフサイクルを、PR 単位のラウンドとして表示する。

- 最初の `opened` / `synchronized` はラウンド 1 として記録する
- 新しい `re-review-requested` を受信するとラウンドを 1 つ進める。同じ event ID は重複して数えない
- reviewer のプロセス実行中は `reviewer 実行中`、正常終了後は `reviewer 実行完了（結果未確認）` と表示する
- 「結果未確認」はレビュー承認を意味しない。Verdict、未解決スレッド、CI、マージ可否の正本は thread-owl / GitHub にある
- ローカル表示状態は `%LocalAppData%\SquirrelNotifier\review-cycles.json` に保存し、7 日間または 100 PR 分を超えた状態は保持しない

この表示はレビュー業務状態の代替ではない。実装 CLI の `review://status/...` 待機がタイムアウトした場合も、Recent activity のローカル実行結果だけからレビュー完了やマージ可否を推測してはならない。

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
| GitHub CLI（`gh`） | PR 状態の自動片付けに必要。PATH に通り、アプリ起動ユーザーで `gh auth login` 済み | 認証済み PR 状態を読み、private repo のマージ・クローズ済みイベントも片付ける。取得失敗時はイベントを保持する。CI 確定待ちにも使う（暫定、[CI の確定待ち](#ci-の確定待ち暫定)）。CI 待機では使えない場合は待たずに起動する |

`MCP_PROBE_URL` は設定しない。thread-owl の route を含む完全 URL で、subscriber の既定
`--url` にもなるため、他の route を購読するときに邪魔になる。

## 自動起動の判定順序

queue event を受け取ると、Squirrel Notifier は次の順に判定する。どこで見送っても
理由を「Recent activity」に記録する。

1. 「レビュー自動開始」が無効 → 起動しない（記録も残さない。無効時の挙動を変えないため）
2. reviewer 側のアクションを伴わない reason → 起動しない
3. PR がマージ・クローズ済み → 起動しない
4. **別のレビューが実行中、または起動処理中** → **保留**し、実行が終わった時点で再評価する
5. **required checks が未確定**（暫定、[CI の確定待ち](#ci-の確定待ち暫定)） → **保留**し、確定または上限まで 30 秒ごとに再評価する
6. **Auto-Pause が Paused** → **保留**し、解除を確認した時点で再評価する
7. いずれにも当たらない → 起動する

手動の「レビューする」は自動起動の判定（1〜5）を通らない。CI の確定も待たず、従来どおり
即時に起動する（別のレビューの実行中は起動しない点と、Auto-Pause の override 確認は従来のまま）。

### 保留と再開

4・5・6 で保留したイベントは PR 単位で 1 件だけ保持され、受信順を保つ。同じ PR の
イベントが後から届いた場合は、順番を保ったまま新しいほうへ差し替える。

再評価の契機は次のとおり。

| 保留の理由 | 再評価の契機 |
|---|---|
| 別のレビューが実行中 | 実行の終了。起動処理がレビューを起動せずに終わった場合はその時点 |
| CI の確定待ち | 30 秒ごと。確定・失敗・取得不能・上限のいずれかで終える |
| Auto-Pause | 解除の確認（レートリミット欄の「更新」、レビューの終了時 snapshot）、およびリセット時刻の通過。そこで解除を確認できない場合は 15 分ごと |

再評価でも上の 1〜7 を通すため、保留中に設定が変わった・PR が閉じた・一覧から消えた
場合は起動しない。保留はメモリ上にのみ持ち、アプリを再起動すると失われる（イベントは
一覧に残るので手動で起動できる）。

CI の確定待ちは PR ごとの判定のため、待っている PR があっても、後ろに保留した別の PR の
評価は止まらない。別のレビューの実行中や Auto-Pause は全 PR に共通の判定なので、先頭で
保留になった時点で後続の評価を止める。

保留したことはトレイ通知にも「レビューを保留中（理由）」として出る。無人運用では
通知が唯一の気づく手段になるため、自動起動しなかっただけのイベントとは文言を分けている。

## CI の確定待ち（暫定）

> **暫定の処置である。** 恒久案は、CI とレビューを並行させ、完了通知の直前にだけ CI の確定を
> 待つ方式である（下の「暫定である理由と、撤去・転用の条件」を参照）。

reviewer は、required checks が未完了の状態で起動すると、CI を `pending` と判定して Verdict を
出さず、コメントだけで完了する。CI の完了後に再依頼が必要になり、`max_cycles` のうち 1 サイクルを
余分に消費する。これを避けるため、Squirrel Notifier は**自動起動の前に CI の確定を待つ**。
この待機は最適化であり、安全性の gate ではない（判定の正本は reviewer と reviewed 側の確認である。
[Verdict と CI の確認](#verdict-と-ci-の確認)）。

### 何を待つか

PR の現在の head SHA に対する、required checks の確定状態を `gh api` で取得する（読み取りのみ）。
読むのは check の名前と状態だけで、コメントの本文などは読まない。

- required checks は、ruleset（`rules/branches/<base>`）を先に、classic の branch protection を後に読み、
  和集合にする。classic の 404（保護設定なし）は「required なし」、403 は「取得不能」とする
- check runs は head SHA 固定で全ページを取得し、同じ App・同じ名前の run は ID が最大のものだけを採用する。
  commit status（Status API）にも required の context があり得るため、両方を見る
- required checks が定義されていないリポジトリでは、**報告済みの check すべて**を対象にする。
  1 件も報告されていなければ、待つ対象が無いため待たない
- required checks に定義があるのに未報告のもの（`codecov/patch` のように、他の check より少し遅れて
  現れる check）は、未完了として待つ

### 終わり方

30 秒ごとに確認する（Recent activity には確認のたびの行を残さない）。上限は、最初に未確定を観測した時点から 12 分
（CI は最長 8 分の見込み）。間隔と上限は定数で、設定項目にはしない。

| 状態 | 動作 | Recent activity |
|---|---|---|
| required checks がすべて success（neutral / skipped を含む） | 起動する | 待った場合だけ「CI が確定しました（待機 N 分 M 秒）」 |
| 失敗が出た | **待たずに起動する**（失敗はレビューの入力になる。未完了の check が残っていても失敗を優先する） | 「CI に失敗があるため、待たずに起動します」 |
| 上限に達した | 起動する。reviewer は従来どおり `CI: pending` と判定する | 「CI 待機の上限に達したため起動します」。通知にも「（CI 待機の上限に達したため起動）」を添える |
| PR が merge / close された | 起動しない | 「PR が merge または close されているため、自動起動しません」 |
| 確認中に、同じ PR の reviewer が起動された（手動の「レビューする」など） | **自動起動を取りやめる**（保留に戻さず、待機の状態も破棄し、通知も出さない。二重に起動しない）。別の PR の起動では取りやめない | 「同じ PR のレビューが起動されたため、自動起動を取りやめます」 |
| head が動いた | 新しい head に対して待ち直す（開始時刻と上限を数え直す）。`synchronized` の event は同じ PR の保留を差し替えるため、二重に起動しない | 「head が更新されたため、新しい head の CI を待ち直します」 |
| 取得不能（`gh` が無い、未ログイン、権限不足、レート制限、応答を解釈できない、など） | **待たずに起動する**（fail-open） | 「CI の状態を取得できないため、待たずに起動します: 原因」 |

前回の確認から上限以上あいた場合（別のレビューが長く実行されていた場合など）は、連続した待機ではないため、待機を最初から数える。

待っている間は「レビューを保留中（CI 完了待ち）」が通知に出る。保留の記録には未完了の check の名前が入る。

30 秒ごとの再評価では、PR の状態（merge / close）を `gh` で毎回確認するため、Recent review events の
終了確認（認証済み `gh api`）は行わない。30 秒ごとの再評価で巡回予算（初期5回＋毎時25回補充）を使い切らないため。

### `gh` の前提

`gh` を PATH から解決し、`gh auth login` 済みのログイン状態で実行する。タスクスケジューラから起動した
トレイアプリは、同じユーザーのログオンセッションで動くため、PATH と資格情報ストアをそのまま使える。
`gh` が使えない場合は、取得不能として待たずに起動するので、従来と同じ挙動になる。

### 暫定である理由と、撤去・転用の条件

この方式は、CI の待ち時間とレビューの時間が直列になるため、壁時計としては無駄が多い。reviewer の
レビュー（実測で 4〜7 分）の間に、CI は終わるか、途中まで進む。

| 方式 | 壁時計 |
|---|---|
| 暫定（この節。起動前に待つ） | CI の時間 + レビューの時間（CI 最長 8 分の見込みで、おおよそ 12〜15 分） |
| 恒久案（CI とレビューを並行し、最終判定の直前でだけ待つ） | max(CI の時間, レビューの時間) + 残りの待ち（多くは 0〜数分） |

恒久案では、reviewer は即時に起動して独立確認を進め、完了通知の直前に CI の確定を 1 回だけ待つ。
待機の手段（Squirrel Notifier のローカル API、thread-owl の待機付き tool など）は未決で、
reviewers-forest のハンドオフ `squirrel-notifier.md` の「トピック 2」にある
「#325（reviewer の CI 完了待ち）の置き場（D3）」で扱っている
（横断の方針は `cross_review-operation-mcp-boundary.md` の「再計画」）。

移行で作り直さずに済むよう、実装は 3 つに分けてある。

| 部品 | 責務 | 移行時 |
|---|---|---|
| `ICiSettleSource`（`GhCiSettleSource`） | head SHA と required checks の確定状態を **1 回**取得する。状態を持たない | thread-owl が CI の状態を返す tool を提供したら、実装ごと差し替え、`gh` への依存と required checks の解決の複製を削除する |
| `CiSettleWaiter` | ポーリング・上限・キャンセル・head の移動。間隔と上限は引数で受ける | 「終端で 1 回だけ待つ」用途に転用する（`WaitAsync`。上限は用途ごとに指定する） |
| `ReviewCiSettleGate` と `ReviewAutoStartPolicy` の「CI 確定待ち」 | 自動起動の判定への薄い接続。保留理由のラベル「CI 完了待ち」もここ | **撤去する**（並行に変えたら、起動前に待つ理由が無くなる） |

撤去・転用は、thread-owl の CI 状態 tool、または終端の待機手段が提供された時点で行う。

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
- `のレビューを保留しました: CI 完了待ち（未完了: …）` → required checks の確定待ち（暫定）。
  30 秒ごとに確認し、確定・失敗・取得不能・上限（12 分）のいずれかで起動する。続く
  `CI が確定しました` / `CI に失敗があるため、待たずに起動します` / `CI の状態を取得できないため、
  待たずに起動します: 原因` / `CI 待機の上限に達したため起動します` の行で終わり方が分かる。取得不能の
  原因が `gh` の未インストール・未ログインなら、`gh auth status` を確認する
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

Squirrel Notifier は自動起動の前に CI の確定を待つ（[CI の確定待ち](#ci-の確定待ち暫定)）が、
これは reviewer が `CI: pending` で完了する無駄を減らすための最適化であり、以下の確認の代わりには
ならない。待機は上限（12 分）で打ち切って起動する。取得不能なら待たずに起動し、手動で起動した
場合は待たない。したがって、いずれの場合も reviewer は従来どおり CI の状態を判定し、
reviewed 側は以下の手順で確認する。

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

required かどうかは check runs の応答に含まれないため、**branch protection / ruleset にある
required status-check の実設定だけを正本として**別途確認する。workflow 内のコメント、job 名、
過去の判断から required / optional を断定してはならない。設定を取得できない、または required
判定が不明な場合は `CI: unknown` とし、マージ判断へ進まない。required status-check に含まれない
job は optional として結果を記録するが、required である可能性を無視してはならない。

check が未返却でも、それだけで失敗とは判定しない。対象 workflow の path フィルタで起動しない
ことを確認し、さらに branch protection / ruleset の required status-check に含まれていないことを
確認する。どちらかを確認できない、または実設定と矛盾する場合は `CI: pending` または
`CI: unknown` としてマージ判断へ進まない。たとえば Changelog Guard の `verify` は
csproj・CHANGELOG・当該 workflow の変更でしか起動しないため、docs のみの PR では実行されないが、
この扱いも required status-check の実設定と照合して判断する。

## 関連

- [auto-pause.md](auto-pause.md): Auto-Pause の判定対象・解除条件・トラブルシューティング
- [launcher-agent-presets.md](launcher-agent-presets.md): プリセットごとの起動引数と resume の仕様
- [statusline-integration.md](statusline-integration.md): レートリミット snapshot の生成
