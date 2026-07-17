# 実行エージェントのプリセット化（#149）

## 責務境界

squirrel-notifier の launcher スロット（reviewer / reviewed）が扱うのは**どのコマンドをどの引数で起動するか**のみである。

各エージェント（claude / codex / agy / copilot）が thread-owl 等の MCP サーバーへ接続するための設定（MCP サーバー登録、認証、ツール allowlist 等）は、squirrel-notifier のスコープ外であり、**Mcp-Docker の「CLI agent 設定自動化」の責務**とする。squirrel-notifier はプリセット選択に伴い MCP 接続設定を書き換えたり検証したりしない。

## プリセット一覧

`Models/LauncherAgentDefinition.cs` の `LauncherAgentCatalog.All` に集約する。新しいエージェントの追加・削除はこのカタログのみを変更すればよい。

| プリセット ID | コマンド | 引数の方式 | rateLimitAgentId |
|---|---|---|---|
| `claude` | `claude` | `-p "/thread-owl-pr-reviewer ..."` のようなスキル呼び出し | `claude-code` |
| `codex` | `codex` | reviewer は `exec --skip-git-repo-check "..."`、reviewed は `exec "..."` にプロンプト全文を埋め込み | `codex`（レートリミット取得は対応待ち。[docs/statusline-integration.md](statusline-integration.md) 参照） |
| `agy` | `agy` | `--print-timeout 30m -p "..."` にプロンプト全文を埋め込み | `agy` |
| `copilot` | `copilot` | `-p "..."` にプロンプト全文を埋め込み | `null`（レートリミット取得手段が無い） |

codex / agy / copilot はスキル呼び出し機構を持たないため、claude 版スキルが行う指示内容（thread-owl MCP のツールを使ったレビュー・対応フロー）をプロンプト全文としてテンプレートに埋め込んでいる。実際に動作させるには、当該エージェントが thread-owl の MCP ツールを利用できるよう接続設定済みであることが前提（前述の責務境界により、この接続設定自体は squirrel-notifier の対象外）。

## Settings UI での挙動

- reviewer / reviewed 各スロットにプリセット選択 ComboBox を用意する。選択すると command / arguments が既定値で上書きされる
- 選択後も command / arguments は自由編集できる。保存時（`LauncherAgentCatalog.ResolvePresetId`）に現在の command / arguments を各プリセットの既定値と突き合わせ、完全一致しなければ「カスタム」として扱う。プリセット選択 ComboBox はこの判定結果を表示するだけで、選択操作そのものを永続化するわけではない
- 既存ユーザーの設定は `LauncherPresetsMigrated` フラグで一回だけ移行し、移行時点の command / arguments がどのプリセットと一致するかを判定して `ReviewerLauncherPresetId` / `ReviewedLauncherPresetId` に記録する
- #180 より前の `agy` 既定引数は CLI 内部の print timeout が 5 分だったため、未変更の既定値だけを `AgyPrintTimeoutMigrated` で `--print-timeout 30m` 付きへ移行する。自由編集された command / arguments は変更しない
- reviewer は対象 checkout 外の専用ディレクトリから起動するため、#186 より前の Codex reviewer 既定引数だけを `CodexReviewerWorkingDirectoryMigrated` で `--skip-git-repo-check` 付きへ移行する。自由編集された command / arguments と reviewed 側は変更しない

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

codex 等スキル機構を持たないエージェントは、プロンプトを引数で受け取っても標準入力の EOF を待って停止することがある（[openai/codex#20919](https://github.com/openai/codex/issues/20919)）。`ReviewLauncherService` は起動直後に標準入力を即座に閉じ、EOF を通知することでこれを回避している。
