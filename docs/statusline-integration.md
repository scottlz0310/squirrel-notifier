# レートリミット監視: statusline 連携（#139）

## 背景

レートリミット解除の事前通知予約機能（#114）が必要とする情報（5時間枠・週次枠の使用率・リセット時刻）は、MCP サーバーが提供するものではなく、**ローカルで動作している CLI エージェントクライアント自身が持つ情報**である。

各エージェントには「statusline」フック（セッション中に外部コマンドへ JSON を stdin 経由で渡し、ターミナルのステータス表示をカスタマイズできる機構）があり、その JSON にレートリミット情報が含まれる。squirrel-notifier はこの情報を MCP 経由ではなく、**ローカルファイル経由**で取得する。

```
[CLI エージェント] --statusline JSON--> [拡張した statusline スクリプト] --共通スキーマで書き出し--> [ローカル JSON ファイル] --読み取り--> [squirrel-notifier]
```

squirrel-notifier リポジトリ側は「ローカルファイルを読み取る機能」のみを実装する。各エージェントの statusline スクリプト自体の拡張（レートリミット検知時のファイル書き出し追加）は、ユーザー自身の dotfiles / env-config 側で行う。このページはそのための手順書。

## 対応エージェント

| エージェント ID | 対応状況 |
|---|---|
| `claude-code` | 対応可能（statusline フックあり） |
| `agy`（Antigravity CLI） | 対応可能（statusline フックあり） |
| `codex` | **対応待ち**。Codex CLI の `tui.status_line` は組み込みアイテムを TUI に表示するだけで外部コマンドに JSON を渡す仕組みがなく、`notify` フックにも rate limit フィールドが無い。機械可読な usage 出力は未実装（[openai/codex#16037](https://github.com/openai/codex/issues/16037)、[#20310](https://github.com/openai/codex/issues/20310)）。実装され次第対応する |

## 書き出し先パス

```
%LOCALAPPDATA%\SquirrelNotifier\ratelimit-status\<agentId>.json
```

`<agentId>` は上表のエージェント ID（`claude-code` / `agy`）。squirrel-notifier の Settings 画面で対応するチェックボックスを ON にすると、このパスを読み取り対象に追加する。

## 共通スキーマ

書き出す JSON は squirrel-notifier の `RateLimitStatusPayload` / `RateLimitInfo` がそのまま読める形式にする。**使用率（%）は squirrel-notifier 側で表示・利用しないため含めなくてよい**。必要なのは `id` / `label` / `resetAt`（ISO 8601 文字列）のみ。

```json
{
  "limits": [
    { "id": "claude-code-5h", "label": "claude-code 5時間枠", "resetAt": "2026-07-06T13:30:00Z" },
    { "id": "claude-code-7d", "label": "claude-code 週次枠",   "resetAt": "2026-07-13T00:00:00Z" }
  ]
}
```

書き込みはアトミックに行う（`.tmp` に書いてから `rename`/`Move-Item`）。squirrel-notifier 自身の `CacheService.SaveAsync` も同じパターンを採用している。

## エージェント別の実データ構造とサンプル

### claude-code

`statusLine` フックの JSON に `rate_limits.five_hour` / `rate_limits.seven_day` が含まれる（`resets_at` は UNIX epoch 秒）：

```json
"rate_limits": {
  "five_hour": { "used_percentage": 73, "resets_at": 1783312200 },
  "seven_day": { "used_percentage": 45, "resets_at": 1783764000 }
}
```

サンプル: [`samples/claude-code-ratelimit-snippet.sh`](samples/claude-code-ratelimit-snippet.sh)（既存の statusline スクリプトの `input=$(cat)` 直後に呼び出しを追記する形の bash 関数）

### agy（Antigravity CLI）

`quota` フィールドに `3p-5h` / `3p-weekly` / `gemini-5h` / `gemini-weekly` の4バケットがあり、それぞれ `reset_time`（ISO 8601 文字列）を持つ：

```json
"quota": {
  "3p-5h":         { "remaining_fraction": 1,      "reset_time": "2026-06-30T07:49:12Z" },
  "3p-weekly":     { "remaining_fraction": 0.615,  "reset_time": "2026-07-04T00:57:20Z" },
  "gemini-5h":     { "remaining_fraction": 1,      "reset_time": "2026-06-30T07:49:12Z" },
  "gemini-weekly": { "remaining_fraction": 1,      "reset_time": "2026-07-07T02:49:12Z" }
}
```

サンプル: [`samples/agy-ratelimit-snippet.ps1`](samples/agy-ratelimit-snippet.ps1)（既存の statusline スクリプトの末尾で呼び出しを追記する形の PowerShell 関数）

## 適用手順

1. 上記サンプルスクリプトの内容を、お使いの statusline スクリプト（例: `~/.claude/statusline-command.sh`、`agy` の `statusline.ps1`）に追記する
2. エージェントを実際に使用し、statusline が一度呼ばれるのを待つ（`%LOCALAPPDATA%\SquirrelNotifier\ratelimit-status\<agentId>.json` が生成されることを確認）
3. squirrel-notifier の Settings 画面で対象エージェントのチェックボックスを ON にする
4. 「レートリミット状態」セクションの「更新」を押す
