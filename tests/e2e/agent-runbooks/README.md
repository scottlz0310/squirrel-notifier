# 実デスクトップ E2E ランブック（AI エージェント向け）

リリース前に、AI エージェントが利用者の実機で Squirrel Notifier を動かして確認するためのランブックです（#433）。
headless CI（`tests/e2e/README.md`）では確かめられない、実デスクトップが要る振る舞いを扱います。
Claude Code 以外のエージェント（Codex / Antigravity など）も、この README とシナリオだけを読んで実行できます。

## 原則

1. **合否は機械的な証跡で決める。** UIA の値、ログ、観測ファイル（dummy launcher / subscriber の JSONL）で判定する。スクリーンショットは補助にとどめ、証跡の無い成功は失敗として扱う
2. **実環境を汚さない。** データディレクトリ・gateway・subscriber・launcher はすべて run ディレクトリに閉じ込める。本番の gateway・queue・PR・資格情報には書き込まない。後始末で漏れを検査する
3. **自己検証をしない。** 対象リリースを実装したセッションとは別のセッションで実行する。結果の `executor.implementerSession` は `false` でなければならない
4. **要素は AutomationId で特定する。** 表示文言に頼らない
5. **人の操作が要る step は明記する。** 無人化は目指さない

## 準備

- Windows 11 の実機（対話デスクトップ）。PowerShell 7 と .NET SDK（`global.json` の版）
- 作業ツリーはクリーンで、HEAD がテスト対象のコミットであること（リリース準備 PR のブランチ）
- 常駐中の Squirrel Notifier は止めなくてよい。テスト用インスタンスはデータディレクトリごとに別インスタンスとして起動する（`SQUIRREL_NOTIFIER_DATA_ROOT`）

## ツール

すべて `tests/e2e/agent-runbooks/tools/` にあります。

| スクリプト | 役割 |
|---|---|
| `Start-RunbookEnvironment.ps1 -Scenario <id>` | build、隔離環境の作成、fake Gateway の起動、テスト用インスタンスの起動。標準出力の JSON（`run.json` と同じ）に PID とパスが入る |
| `Invoke-RunbookUia.ps1 -ProcessId <pid> -Action <action> -AutomationId <id>` | 要素の読み取り・クリック・入力・待機。`-EvidencePath` で証跡を JSONL に追記する |
| `Stop-RunbookEnvironment.ps1 -RunDirectory <dir>` | プロセスの停止と、実データへの漏れの検査（`evidence/isolation.json`）。漏れがあれば exit 1 |

`Start-RunbookEnvironment.ps1` は次を確かめてから起動します。満たさなければ起動せずに失敗します。

- 作業ツリーがクリーンであること
- exe の ProductVersion が `+<HEAD の SHA>` で終わること（古いビルドはデータディレクトリの切り替えが効かず、実データへ書き込むため）

テスト用インスタンスの環境は次のとおりです。

- 購読先: fake Gateway（loopback）
- subscriber: dummy。PR #307〜#309 の固定イベントを約 5 秒ごとに返す
- reviewer / reviewed: dummy launcher（`codex.exe` という名前の fixture）。起動の記録を `observations/launcher.jsonl` に残す

ツールは必ず `pwsh -File` で別プロセスとして実行してください。環境変数を呼び出し元のシェルへ残さないためです。

## 操作の注意（観測済みの事象）

- **ボタンは `-Action Click`（実際のマウス入力）で押す。** UIA の InvokePattern だけで reviewer の起動と購読の停止を続けて行うと、アプリの UI スレッドが応答しなくなる事象を 3 回中 2 回観測した。マウス入力では再現していない。`Click` はクリック位置の要素が対象プロセスのものか確かめてから押すので、同じタイトルの常駐インスタンスを誤って押さない
- **ウィンドウ全体を子孫まで何度も走査しない。** 一覧の項目は `-ListId` と `-ItemText` で絞って探す
- **購読の停止は `StatusText` ではなくボタンの状態で判定する。** 停止しても `StatusText` は `Subscribed.` のままになる
- **通知ポップアップはフォーカスを失うと閉じる。** 起動直後、他のウィンドウを操作する前に読む
- fixture の PR（`fixture-owner/fixture-repository` など）に対して、アプリは GitHub API へ PR 状態を読みに行き 404 を受ける。読み取りだけで、書き込みは起きない
- 操作のたびに `(Get-Process -Id <pid>).Responding` を確かめ、`False` が 10 秒以上続いたら flaky として記録し、後始末してそのシナリオを再実行する

## 実行手順

1. `scenarios/` の必須シナリオ（`required: true`）を順に実行する。各シナリオは独立しており、失敗したシナリオだけを再実行できる
2. シナリオごとに `Start-RunbookEnvironment.ps1` で始め、`Stop-RunbookEnvironment.ps1` で終える。`isolation.json` の `isolated` が `true` でなければ、そのシナリオは fail
3. 結果を `results/v<version>.json` に書く（下記）。証跡のファイル自体はコミットしない。要点を `summary` に書き写す
4. リリース準備 PR に結果だけをコミットして push する。`runbook-guard` がテストしたコミットと HEAD の差分を検査するため、結果以外の変更を同じコミットに入れない

コードを直したら、そのコミットでランブックを実行し直してください。

## 結果の記録形式（`results/v<version>.json`）

```json
{
  "schemaVersion": 1,
  "version": "0.15.0",
  "testedCommit": "<40 桁の SHA。Start-RunbookEnvironment.ps1 の commit>",
  "productVersion": "0.15.0+<SHA>",
  "executedAt": "2026-09-27T05:00:00Z",
  "executor": { "agent": "claude-code", "model": "<モデル名>", "implementerSession": false },
  "environment": { "os": "Windows 11 Pro 10.0.26200", "notes": "" },
  "scenarios": [
    {
      "id": "review-event-flow",
      "result": "pass",
      "attempts": 1,
      "durationSeconds": 95,
      "evidence": [
        { "kind": "uia", "path": "evidence/uia.jsonl", "summary": "TitleText=re-review-requested: third-owner/third-repository#309" },
        { "kind": "observation", "path": "observations/launcher.jsonl", "summary": "arguments=[review, fixture-owner/fixture-repository#307, opened]" },
        { "kind": "isolation", "path": "evidence/isolation.json", "summary": "isolated=true" }
      ],
      "flaky": []
    }
  ]
}
```

- `result` は `pass` / `fail` / `skipped`。`runbook-guard` は必須シナリオがすべて `pass` であることを求める
- `evidence[].kind` は `uia` / `log` / `observation` / `isolation` / `screenshot`。`pass` にはスクリーンショット以外の証跡が 1 つ以上要る
- `flaky` には、再実行で通った失敗や、合否に影響しない異常（応答なし・ダイアログの取り残しなど）を書く。`attempts` が 2 以上なら必ず書く

## 入口

Claude Code では `/desktop-e2e-runbook`（`.claude/skills/desktop-e2e-runbook/SKILL.md`）から実行できます。skill はこの README を読ませるだけの薄い入口です。
