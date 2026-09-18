# セッション resume の E2E 検証手順

Issue #242 の launcher session 引き継ぎを、開発ビルドの Squirrel Notifier で検証する手順です。

## E2E-A: resume smoke

1. `SessionResumeEnabled` を有効にし、reviewer launcher を Claude Code に設定する。
2. 設定画面の `Reviewer Args` と `Reviewer Resume Args` を、次の完全な引数へ一時変更する。
   - `Reviewer Args`: `-p "この会話の合言葉を復唱してください。合言葉のみを答えてください。" --session-id {sessionId} --verbose --output-format stream-json`
   - `Reviewer Resume Args`: `-p "この会話の合言葉は何でしたか。合言葉のみを答えてください。" --resume {sessionId} --verbose --output-format stream-json`
3. 同じ review event で「レビューする」を 1 回実行し、`sessions.json` の `sessionId` と起動コマンドの値が一致することを確認する。
4. 同じ event で「レビューする」をもう一度実行する。
5. 2 回目の起動が `--resume <sessionId>` を使い、1 回目の合言葉を答えることを確認する。

確認時は session ID をログや Issue コメントへ全文転記せず、先頭数文字だけを記録します。

## E2E-C: fallback

次の各条件で、resume ではなく新規起動へフォールバックすることを確認します。

- 保存エントリの TTL を超過させる。
- 保存時と異なる working directory にする。
- 保存 session ID は D 形式の有効な UUID のままにし、CLI / dummy 側でその UUID に対応する session が存在しない状態にする。`--resume` 起動の非ゼロ終了、エントリ破棄、UI 表示、同一実行内での自動再試行なしを確認する。形式不正の UUID を保存するケースは、この resume 失敗経路の検証対象にしない。
- `SessionResumeEnabled` を無効にする。起動コマンドに resume 引数がなく、`sessions.json` が更新されないことを確認する。

各ケースの終了後は、テスト前に退避した設定と session store を復元します。
