# レートリミット監視: statusline 連携（#139、#145）

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
| `codex` | **statusline 連携は不要**（このページの手順の対象外）。Codex CLI の `tui.status_line` は外部コマンドに JSON を渡す仕組みがない（[openai/codex#16037](https://github.com/openai/codex/issues/16037)、[#20310](https://github.com/openai/codex/issues/20310)）が、squirrel-notifier が Codex App Server（`codex app-server` の `account/rateLimits/read`）から直接取得する（spike #157 / #163）。`codex` にログイン済みであれば追加設定なしで動作する。App Server 経由の snapshot は取得時刻が観測時刻になるため常に fresh であり、ヘッドレス実行でも Delta・Auto-Pause の解除判定が機能する |

## 書き出し先パス

```
%LOCALAPPDATA%\SquirrelNotifier\ratelimit-status\<agentId>.json
```

`<agentId>` は上表のエージェント ID（`claude-code` / `agy`）。squirrel-notifier の Settings 画面で対応するチェックボックスを ON にすると、このパスを読み取り対象に追加する。

## 共通スキーマ（schemaVersion 1）

書き出す JSON は squirrel-notifier の `RateLimitStatusPayload` / `RateLimitInfo` がそのまま読める形式にする。

```json
{
  "schemaVersion": 1,
  "agentId": "claude-code",
  "observedAt": "2026-07-11T13:30:00Z",
  "limits": [
    { "id": "claude-code-5h", "label": "claude-code 5時間枠", "resetAt": "2026-07-06T13:30:00Z", "usedPercentage": 73 },
    { "id": "claude-code-7d", "label": "claude-code 週次枠",   "resetAt": "2026-07-13T00:00:00Z", "usedPercentage": 45 }
  ]
}
```

| フィールド | 必須 | 説明 |
|---|---|---|
| `schemaVersion` | 新形式では必須 | `1` 固定。使用率・Delta・freshness 判定を有効にする印 |
| `agentId` | 新形式では必須 | 上表のエージェント ID |
| `observedAt` | 新形式では必須 | この snapshot の観測時刻（ISO 8601） |
| `limits[].id` / `label` / `resetAt` | 必須 | 従来どおり |
| `limits[].usedPercentage` | 任意（0〜100） | エージェント固有の残量表現（agy の `remaining_fraction` 等）はスクリプト側で使用率へ正規化してから書き出す |

`schemaVersion` / `agentId` / `observedAt` を省略した**旧形式（resetAt-only）の payload も引き続き読み取れる**（後方互換）。ただし旧形式は通知予約（リマインダー）用途のみに使われ、使用率表示・Delta・freshness・Auto-Pause（#147）の判定対象には含まれない。

書き込みはアトミックに行う（`.tmp` に書いてから `rename`/`Move-Item`）。squirrel-notifier 自身の `CacheService.SaveAsync` も同じパターンを採用している。

## 旧形式のまま残っている場合の警告（#168）

`schemaVersion` / `agentId` / `observedAt` を欠く旧形式の payload は、レートリミット一覧（`RateLimitStatusParser.Parse`）には引き続き表示されるため、一見「監視できている」ように見える。しかし `RateLimitSnapshotService` は旧形式に対して常に `null` を返すため、**Auto-Pause（#147）は使用率が 95% を超えても一切発動しない**。

squirrel-notifier は「レートリミット状態」セクションの「更新」を押した際に旧形式を検出すると、`statusline snapshot が旧形式です` という警告 InfoBar を表示する（`RateLimitStatusParser.IsLegacySchema`）。この警告が出た場合は Auto-Pause が機能していないため、以下の移行手順に従って statusline スクリプトを更新すること。

### 移行手順

1. 既存の statusline スクリプト（`~/.claude/statusline-command.sh`、`agy` の `statusline.ps1` 等）が resetAt-only の旧形式で書き出していないか確認する（`schemaVersion` フィールドが JSON に含まれていなければ旧形式）
2. 上記「エージェント別の実データ構造とサンプル」のサンプルスクリプト（[`samples/claude-code-ratelimit-snippet.sh`](samples/claude-code-ratelimit-snippet.sh) / [`samples/agy-ratelimit-snippet.ps1`](samples/agy-ratelimit-snippet.ps1)）の内容で書き出し部分を置き換え、`schemaVersion` / `agentId` / `observedAt` / `usedPercentage` を含む新スキーマに揃える
3. エージェントを実際に使用し、statusline が一度呼ばれるのを待つ
4. squirrel-notifier で「レートリミット状態」の「更新」を押し、警告 InfoBar が消えることを確認する

## Delta（レビューサイクル単位の使用率差分）と freshness

squirrel-notifier はレビューセッション開始時点と終了時点の 2 つの snapshot から、limit ごとの使用率差分（Delta）を算出できる（`RateLimitDeltaCalculator`）。

**重要な制約**: launcher が起動するのは `claude -p` 等の**非対話（ヘッドレス）実行**であり、statusline はインタラクティブセッションの表示機構のため、ヘッドレス実行中に statusline が発火する保証はない。そのため実運用では、レビューセッション開始・終了のタイミングに近い fresh な snapshot が存在せず、Delta が「取得不可」になることが多い。

- Delta は **best-effort** の機能であり、「取得不可」は例外ではなく正常系として扱われる
- snapshot は観測時刻（`observedAt`）が現在時刻から Settings の「freshness 閾値」（既定 15 分、変更可）以内でなければ fresh とみなされない
- 開始・終了の間でリセット境界を跨いだ場合（`resetAt` が変化した場合）も Delta は算出しない（負値や実態と無関係な大量消費として誤表示することを防ぐため）

Claude Code の `Stop` / `SessionEnd` hook は、ヘッドレス実行後の snapshot 更新手段には使えない。公式の hook 入力仕様に `rate_limits` は含まれず、Claude Code v2.1.207 の `SessionEnd` 実測でも `cwd`、`hook_event_name`、`prompt_id`、`reason`、`session_id`、`transcript_path` だけが渡された（#159）。`Stop` は通常応答の完了時のみ発火し、API エラー時は `StopFailure` が発火する。したがって、ヘッドレス実行の Delta は引き続き best-effort とし、取得不可を正常系として扱う。

## エージェント別の実データ構造とサンプル

### claude-code

`statusLine` フックの JSON に `rate_limits.five_hour` / `rate_limits.seven_day` が含まれる（`resets_at` は UNIX epoch 秒、`used_percentage` は 0〜100）：

```json
"rate_limits": {
  "five_hour": { "used_percentage": 73, "resets_at": 1783312200 },
  "seven_day": { "used_percentage": 45, "resets_at": 1783764000 }
}
```

サンプル: [`samples/claude-code-ratelimit-snippet.sh`](samples/claude-code-ratelimit-snippet.sh)（既存の statusline スクリプトの `input=$(cat)` 直後に呼び出しを追記する形の bash 関数。`used_percentage` をそのまま `usedPercentage` として書き出す）

### agy（Antigravity CLI）

`quota` フィールドに `3p-5h` / `3p-weekly` / `gemini-5h` / `gemini-weekly` の4バケットがあり、それぞれ `reset_time`（ISO 8601 文字列）と `remaining_fraction`（残量割合 0〜1）を持つ：

```json
"quota": {
  "3p-5h":         { "remaining_fraction": 1,      "reset_time": "2026-06-30T07:49:12Z" },
  "3p-weekly":     { "remaining_fraction": 0.615,  "reset_time": "2026-07-04T00:57:20Z" },
  "gemini-5h":     { "remaining_fraction": 1,      "reset_time": "2026-06-30T07:49:12Z" },
  "gemini-weekly": { "remaining_fraction": 1,      "reset_time": "2026-07-07T02:49:12Z" }
}
```

`remaining_fraction` は squirrel-notifier の `usedPercentage`（使用率）とは向きが逆（残量割合）のため、サンプルスクリプト側で `(1 - remaining_fraction) * 100` に正規化してから書き出す。

サンプル: [`samples/agy-ratelimit-snippet.ps1`](samples/agy-ratelimit-snippet.ps1)（既存の statusline スクリプトの末尾で呼び出しを追記する形の PowerShell 関数）

## 適用手順

1. 上記サンプルスクリプトの内容を、お使いの statusline スクリプト（例: `~/.claude/statusline-command.sh`、`agy` の `statusline.ps1`）に追記する
2. エージェントを実際に使用し、statusline が一度呼ばれるのを待つ（`%LOCALAPPDATA%\SquirrelNotifier\ratelimit-status\<agentId>.json` が生成されることを確認）
3. squirrel-notifier の Settings 画面で対象エージェントのチェックボックスを ON にする
4. 「レートリミット状態」セクションの「更新」を押す
