# エージェント実行の progress event contract（#143）

## 背景

squirrel-notifier の launcher は、エージェント実行中の stdout / stderr を逐次読み取り、UI 層へ型付きイベント（stdout / stderr / progress / completed）として配信する（`AgentExecutionSession`）。

このうち **progress**（phase 表示・Verdict）は自然言語ログからの推測では扱わず、エージェント（またはラッパー）が明示的に出力する構造化イベントのみを解釈する。このページはその contract（producer が出力すべき形式）を定義する。

```
[エージェント / スキル] --stdout に @squirrel-progress JSONL を混在--> [squirrel-notifier ReviewLauncherService]
    --型付きイベント--> [AgentExecutionSession] --購読--> [UI（ライブログウィンドウ #144）]
```

squirrel-notifier リポジトリ側は「マーカー行を解釈する機能」のみを実装する。各エージェントのスキル定義への出力指示の組み込みは、ユーザー自身の dotfiles / skills 側で行う（レートリミットの [statusline 連携](statusline-integration.md) と同じ責務分担）。

## 輸送路

progress event は **stdout に混在する、行頭マーカー付きの 1 行 JSON（JSONL）** として出力する。

- 行頭マーカー: `@squirrel-progress `（半角スペース 1 つを含む完全一致、大文字小文字区別あり）
- マーカーの直後から行末までが JSON オブジェクト 1 個
- 1 イベント = 1 行。改行を含む JSON は不可

```
@squirrel-progress {"schemaVersion":1,"phaseIndex":3,"totalPhases":8,"phaseLabel":"修正","message":"accept 2件を修正中","timestamp":"2026-07-11T10:00:00Z"}
```

通常ログとの偶然の衝突は、行頭マーカーと schemaVersion 検証の二段構えで排除する。マーカーに一致しない行・JSON として不正な行・検証に通らない行は、すべて**通常の stdout ログとしてそのまま流れる**（launcher の実行自体は失敗しない）。

## schema v1

| フィールド | 型 | 必須 | 説明 |
|---|---|---|---|
| `schemaVersion` | int | ✅ | `1` 固定。将来の互換性のないスキーマ変更でインクリメントする。未知のバージョンは通常ログとして扱われる |
| `phaseIndex` | int | ✅ | 現在の phase の **0 始まり**インデックス（0 以上） |
| `totalPhases` | int | ✅ | ワークフロー全体の phase 数（1 以上） |
| `phaseLabel` | string | ✅ | phase の表示名（空白のみは不可）。例: `"修正"`, `"Verdict 待機"` |
| `message` | string | — | phase 内の補足メッセージ |
| `verdict` | string | — | レビュー Verdict が確定した時点で含める。例: `"APPROVED"` |
| `timestamp` | string (ISO 8601) | — | producer 側のイベント時刻。省略時は squirrel-notifier 側の受信時刻のみが使われる |

phase 構成はエージェント非依存であり、phase 数を固定しない。「Phase 0〜8」は claude のレビューワークフロー（thread-owl スキル）における一例であり、他のエージェント・ワークフローは独自の phase 構成で出力してよい（#149 のエージェント差し替えを許容するため）。

## producer 統合（スキルへの組み込み）

スキル定義に組み込むスニペットは [`samples/skill-progress-snippet.md`](samples/skill-progress-snippet.md) を参照。スキルの各 Phase 開始時にマーカー行を echo させることで、squirrel-notifier 側の phase 表示が動作する。

構造化イベントを出力しないランチャー・エージェントはそのまま動作し、phase 表示のない indeterminate な「実行中」として扱われる。

## consumer 側の挙動（参考）

- `ReviewLauncherService` は stdout を行単位で読み取り、`ProgressEventParser` がマーカー行を `AgentProgressEvent` へ変換する
- 有効な progress 行は `Progress` イベントとして配信され、`Stdout` イベントとしては重複配信されない（`LauncherResult.Stdout` の集約には生の行として含まれる）
- 実行終了時は成功 / 失敗 / キャンセル / タイムアウトを区別した terminal event（`Completed`）が必ず 1 回配信される
