# 実行エージェントのプリセット化（#149）

## 責務境界

squirrel-notifier の launcher スロット（reviewer / reviewed）が扱うのは**どのコマンドをどの引数で起動するか**のみである。

各エージェント（claude / codex / agy / copilot）が thread-owl 等の MCP サーバーへ接続するための設定（MCP サーバー登録、認証、ツール allowlist 等）は、squirrel-notifier のスコープ外であり、**Mcp-Docker の「CLI agent 設定自動化」の責務**とする。squirrel-notifier はプリセット選択に伴い MCP 接続設定を書き換えたり検証したりしない。

## プリセット一覧

`Models/LauncherAgentDefinition.cs` の `LauncherAgentCatalog.All` に集約する。新しいエージェントの追加・削除はこのカタログのみを変更すればよい。

| プリセット ID | コマンド | 引数の方式 | rateLimitAgentId |
|---|---|---|---|
| `claude` | `claude` | `-p "/thread-owl-pr-reviewer ..." --verbose --output-format stream-json` のようなスキル呼び出し。stream-json は progress event の逐次取得用で、`-p` との併用時は CLI 仕様で `--verbose` が必須（[docs/progress-event-contract.md](progress-event-contract.md) 参照） | `claude-code` |
| `codex` | `codex` | reviewer は `exec --skip-git-repo-check --json "/thread-owl-pr-reviewer ..."`、reviewed は `exec --json "/review-raven-thread-owl-cycle ..."` | `codex`（レートリミット取得は対応待ち。[docs/statusline-integration.md](statusline-integration.md) 参照） |
| `agy` | `agy` | `--print-timeout 30m --output-format stream-json -p "/thread-owl-pr-reviewer ..."` または `"/review-raven-thread-owl-cycle ..."` | `agy` |
| `copilot` | `copilot` | `-p "/thread-owl-pr-reviewer ..."` または `"/review-raven-thread-owl-cycle ..."` | `null`（レートリミット取得手段が無い） |

全プリセットが正式 skill 名をプロンプトの先頭で呼び出す。skill 本体の配布・更新と MCP 接続設定は Mcp-Docker の責務であり、squirrel-notifier は起動テンプレートだけを管理する。skill が未配布、または CLI が slash skill に未対応の場合は、起動プロセスの stderr と失敗メッセージに原因を残す。

## Settings UI での挙動

- reviewer / reviewed 各スロットにプリセット選択 ComboBox を用意する。選択すると command / arguments が既定値で上書きされる
- 選択後も command / arguments / resume arguments は自由編集できる。保存時（`LauncherAgentCatalog.ResolvePresetId`）に現在の 3 値を各プリセットの既定値と突き合わせ、完全一致しなければ「カスタム」として扱う。プリセット選択 ComboBox はこの判定結果を表示するだけで、選択操作そのものを永続化するわけではない
- 既存ユーザーの設定は `LauncherPresetsMigrated` フラグで一回だけ移行し、移行時点の command / arguments がどのプリセットと一致するかを判定して `ReviewerLauncherPresetId` / `ReviewedLauncherPresetId` に記録する
- #180 より前の `agy` 既定引数は CLI 内部の print timeout が 5 分だったため、未変更の既定値だけを `AgyPrintTimeoutMigrated` で `--print-timeout 30m` 付きへ移行する。自由編集された command / arguments は変更しない
- reviewer は対象 checkout 外の専用ディレクトリから起動するため、#186 より前の Codex reviewer 既定引数だけを `CodexReviewerWorkingDirectoryMigrated` で `--skip-git-repo-check` 付きへ移行する。自由編集された command / arguments と reviewed 側は変更しない
- #187 より前の `claude` 既定引数は text 出力のため progress event を実行中に取得できなかった。未変更の既定値だけを `ClaudeStreamJsonMigrated` で `--verbose --output-format stream-json` 付きへ移行する。自由編集された command / arguments は変更しない
- #306 より前の `codex` / `agy` 既定引数は session ID を取得できない出力形式だった。未変更の既定値だけを `CodexJsonOutputMigrated` / `AgyStreamJsonMigrated` で `codex --json` / `agy --output-format stream-json` 付きへ移行する。自由編集された command / arguments は変更しない
- #353 より前の `codex` / `agy` / `copilot` 既定引数は MCP ツールの使い方を全文へ埋め込んでいた。未変更の既定値だけを `LauncherSkillPromptMigrated` で正式 skill 呼び出しへ移行する。自由編集された command / arguments / resume arguments は変更しない

## セッション resume

「前回セッションを引き継ぐ（resume）」は既定で無効である。無効時は `sessions.json` を読み書きせず、起動コマンドも resume 導入前と同じになる。有効時は同じ repository / PR / role / agent / working directory の保存済み session があれば role 別の resume arguments を使用する。

| プリセット | CLI の session ID 供給方式 | 現在のアプリ対応 | 新規起動 | 2 回目以降 |
|---|---|---|---|---|
| `claude` | `ClientAssigned` | 対応 | `--session-id {sessionId}` | `--resume {sessionId}` |
| `copilot` | `ClientAssigned` | 対応 | `--session-id {sessionId}` | `--session-id {sessionId}` |
| `codex` | `ParsedFromOutput` | 対応 | 通常起動（`thread.started` の `thread_id` を保存） | `exec resume --json {sessionId}` |
| `agy` | `ParsedFromOutput` | 対応 | 通常起動（`conversation_id` を保存） | `--conversation {sessionId}` |

`{sessionId}` は D 形式 UUID（例: `01234567-89ab-cdef-0123-456789abcdef`）だけを受け付ける。完全な値は実行コマンドと「コマンドをコピー」の出力に必要だが、永続ログとライブログには `resumed session 01234567…` のように先頭 8 文字だけを出す。

`codex --json` の既知 JSONL event と `agy --output-format stream-json` の既知 event は、人間向けの agent response だけをライブログへ展開する。session ID が欠落または D 形式 UUID でない場合は理由をログへ残して保存せず、未知 event は生の stdout 行へフォールバックする。通常引数が既定値と完全一致する場合だけこの構造化出力を有効にする。

プリセット選択時は reviewer / reviewed の resume arguments も既定値へ戻る。resume arguments を編集すると、通常 arguments と同様にプリセット表示は「カスタム」へ変わる。カスタム設定は次を満たす場合だけ `ClientAssigned` として扱う。

- resume arguments が空でなく `{sessionId}` を含む
- command が `claude` / `copilot` と一致して既知の新規 session 引数を利用できる、または通常 arguments 自身が `{sessionId}` を含む

任意 CLI の新規 session ID 指定方法をコマンド名から推測して `--session-id` を付けることはしない。Settings UI の各スロットには、この判定結果を「resume 対応 / 非対応」として表示する。

session 情報は `%LOCALAPPDATA%\SquirrelNotifier\sessions.json` に保存する。TTL は最終利用から 7 日、上限は 100 件で、古い entry から削除する。TTL 切れ、working directory 不一致、保存 entry なし、非対応設定では理由を永続ログへ残して新規 session を起動する。resume 起動が失敗した場合は entry を破棄し、ライブログの InfoBar に「次回は新規セッションで起動する」旨を表示する。同一実行内で新規 session へ自動 retry はしない。

## 作業ディレクトリ契約

- reviewer は Settings 保存先配下の `launcher-workspace/reviewer` を作成し、常にそこから起動する。対象 repository の checkout mapping は参照しない
- reviewed は Settings の Checkout Mappings（`owner/repo=絶対パス`）から対象 repository の Git checkout を解決し、そこから起動する
- reviewed の mapping が無い、パスが存在しない、`.git` を持たない、または Windows／Program Files／アプリのインストール先配下の場合は、launcher プロセスを起動する前に失敗する
- `ProcessStartInfo.WorkingDirectory` を常に明示するため、タスクスケジューラー、ショートカット、親プロセスの current directory には依存しない
- 実効 working directory、解決済み executable、executable kind、終了コードを永続ログへ記録する。非ゼロ終了時は stderr の先頭3行（最大500文字）をサニタイズ・マスクして要約記録する

## rateLimitAgentId の解決

`SettingsService.ResolveLauncherRateLimitAgentId(LauncherRole)` が、指定したスロットに選択されているプリセットの `rateLimitAgentId` を返す。「カスタム」設定、および取得手段が無いプリセット（copilot）では `null` を返し、Auto-Pause（#147）はこれを gate 対象外として扱う。

## コマンド解決と Windows shim の起動規約

コマンドパスの解決と `.cmd` / `.bat` shim の起動は、レビュー起動（`ReviewLauncherService`）とレートリミット取得（`CodexAppServerRateLimitClient`）で共通の実装を使う（#186）。

- **解決**（`Helpers/CommandPathResolver`）: 既存ファイルへの直接パス指定を最優先し、次に PATH の各ディレクトリを PATHEXT の優先順で探索する。Win32 `CreateProcessW` は拡張子省略時に `.exe` しか暗黙補完しないため、npm / pnpm 経由の `.cmd` shim（例: `codex.cmd`）もここで明示的に解決する（#177）
- **起動**（`Helpers/AgentProcessStartInfoFactory`）: ネイティブ実行形式は `ArgumentList` で直接起動する。`.cmd` / `.bat` は `cmd.exe /d /s /v:off /c` で明示的にラップし、実行パスと各引数は環境変数を引用符内で一度だけ展開する方式で渡す。cmd.exe の変数展開は展開結果を再解釈しないため、引数に含まれる `%` / `&` / `|` 等のメタ文字が安全に素通しされる（BatBadBut パターンの回避）。引用符・改行を含む引数のみ、cmd.exe の引用状態を破壊しうるため起動前に明示エラーで拒否する

## codex exec のハング対策

codex 等の非対話 print / exec モードは、プロンプトを引数で受け取っても標準入力の EOF を待って停止することがある（[openai/codex#20919](https://github.com/openai/codex/issues/20919)）。`ReviewLauncherService` は起動直後に標準入力を即座に閉じ、EOF を通知することでこれを回避している。
