# Auto-Pause（危険水域での新規エージェント起動停止）

Issue #147 で導入された、レートリミット危険水域での新規 launcher 起動停止機能の運用手順。

## 動作概要

- 起動しようとしている launcher スロット（reviewer / reviewed）のプリセットに対応する
  rateLimitAgentId（#149）の fresh な snapshot を、起動直前に評価する
- fresh な limit のうちいずれか 1 つでも使用率が **95% 以上**なら Paused へ遷移し、新規起動を拒否する
- Paused の理由（agent・limit・使用率・観測時刻・リセット時刻）は起動時のダイアログ、
  メイン UI の「レートリミット状態」セクション、ライブログウィンドウに表示される
- gate は **agent 単位**で独立している。claude-code が Paused でも、agy プリセットのスロットは起動できる

## gate の対象外（常に起動を許可）

- rateLimitAgentId を持たないプリセット（copilot など取得手段が無いエージェント）
- 「カスタム」設定のスロット
- Paused でない agent の snapshot が stale / missing の場合（推測制御をしない）
- 自律実行に使われない予約枠（下記「判定対象の枠」、#335）

## 判定対象の枠

Codex は App Server（`account/rateLimits/read`）から複数の bucket を返す。Auto-Pause は
**通常枠（bucket ID `codex`）の primary / secondary だけ**を判断材料にする。

| 枠 | bucket ID : slot | 表示 | Auto-Pause |
| --- | --- | --- | --- |
| 5 時間制限（全モデル） | `codex:primary` | `Codex — 5時間制限（全モデル）` | 対象 |
| Weekly 制限（全モデル） | `codex:secondary` | `Codex — Weekly制限（全モデル）` | 対象 |
| Luna Reserve Weekly 制限 | `base_model_inference:primary` | `Codex — Luna Reserve Weekly制限（Luna専用・Auto-Pause対象外）` | 対象外 |

- 判定は bucket ID と window slot の組み合わせで行う。表示名や `windowDurationMins` には依存しない
- 通常枠が閾値未満なら、Luna Reserve Weekly が 100% でも起動する
- 予約枠は情報表示とリマインダー予約にのみ使う。自動モデル切替・フォールバック・consume 系 API の
  呼び出しには使わない（アプリは読み取り 2 要求 `initialize` / `account/rateLimits/read` しか送らない）
- 将来 **bucket または slot が追加**された場合、その枠は用途を確認できるまで対象外として扱う
  （誤って起動を止めない）。通常枠に `tertiary` などの slot が増えた場合も同様に対象外になる
- 旧形式の単一 bucket payload（`rateLimits`）は通常枠として扱う
- statusline 由来の snapshot（claude-code / agy）は枠の区別を持たないため、すべて対象

## 解除条件

- **自動解除**: fresh な snapshot（既定 15 分以内、Settings の `RateLimitFreshnessThresholdMinutes`）で
  使用率 95% 未満を確認した場合のみ
- stale / missing data、および resetAt の通過だけでは解除されない
- **手動 override**: Paused ダイアログの「今回だけ起動を強行」で、Paused 状態を維持したまま
  1 回だけ起動できる（既定ボタンはキャンセル側）

### 「レビュー自動開始」で保留したイベントの再開（#340）

Auto-Pause を理由に自動起動を見送ったレビューイベントは保留され、解除を確認した時点で
自動起動が再評価される。解除には fresh な snapshot が必要で、Paused 中は launcher が
起動されないため snapshot が更新されないことがある（上記「Paused から復帰しない」）。
そのため保留中は次の契機で再評価する。

- 根拠となった limit の `resetAt` を過ぎた時点（`resetAt` を取得できない場合は 15 分後）
- そこで解除を確認できなかった場合は、以降 15 分ごと
- レートリミット欄の「更新」、または実行の終了時 snapshot で解除を確認した時点

再評価でも通常の自動起動と同じ判定を通すため、解除前にマージ・クローズされた PR や
一覧から削除されたイベントは起動しない。

## 影響を受けないもの

Auto-Pause は起動可否の判定のみを行う。以下には一切作用しない。

- 実行中のエージェントプロセス（強制終了しない）
- MCP resource の購読
- thread-owl の queue

## トラブルシューティング

### Paused から復帰しない

Paused 中は launcher が起動されないため新しい snapshot が生成されず、レートリミットが
リセットされた後も Paused が続くことがある（さらにヘッドレス実行では statusline 自体が
発火しない。`docs/statusline-integration.md` の制約を参照）。復帰手段は次のいずれか。

1. **対象エージェントを手動で 1 回実行する**（インタラクティブセッション）。statusline が
   fresh な snapshot を書き出し、次回起動時の評価で 95% 未満なら自動解除される
2. **「今回だけ起動を強行」を使う**。実行の終了時 snapshot が fresh で 95% 未満なら、
   その時点で自動解除される（ヘッドレス実行では snapshot が更新されない場合がある）
3. **アプリを再起動する**。Paused 状態はメモリ内のみで永続化されないため初期化される。
   ただし直後の起動評価で fresh な危険 snapshot があれば再び Paused になる

「レビュー自動開始」が Auto-Pause を理由に保留したレビューは、上のどの手段で解除しても
自動で起動される。復帰のために手動で「レビューする」を押す必要はない（#340。ただし
3 の再起動では保留自体も失われるため、イベント一覧から手動で起動する）。


### Paused にならない（なってほしいのに）

- snapshot が stale / missing の場合、未 Paused の agent は Pause されない（誤検知防止）。
  statusline 連携（`docs/statusline-integration.md`）が有効か、
  `<設定ディレクトリ>/ratelimit-status/<agentId>.json` が更新されているかを確認する
- launcher スロットが「カスタム」またはrateLimitAgentId を持たないプリセットの場合、
  gate の対象外になる。Settings のプリセット選択を確認する
