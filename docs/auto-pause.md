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

## 解除条件

- **自動解除**: fresh な snapshot（既定 15 分以内、Settings の `RateLimitFreshnessThresholdMinutes`）で
  使用率 95% 未満を確認した場合のみ
- stale / missing data、および resetAt の通過だけでは解除されない
- **手動 override**: Paused ダイアログの「今回だけ起動を強行」で、Paused 状態を維持したまま
  1 回だけ起動できる（既定ボタンはキャンセル側）

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

### Paused にならない（なってほしいのに）

- snapshot が stale / missing の場合、未 Paused の agent は Pause されない（誤検知防止）。
  statusline 連携（`docs/statusline-integration.md`）が有効か、
  `<設定ディレクトリ>/ratelimit-status/<agentId>.json` が更新されているかを確認する
- launcher スロットが「カスタム」またはrateLimitAgentId を持たないプリセットの場合、
  gate の対象外になる。Settings のプリセット選択を確認する
