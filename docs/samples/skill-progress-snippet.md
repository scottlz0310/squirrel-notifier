# スキル組み込み用 progress event スニペット

レビュー用スキル（`thread-owl-pr-reviewer` / `review-raven-thread-owl-cycle` 等）の SKILL.md に以下のセクションを追記すると、squirrel-notifier から起動した実行で phase 表示が動作する（contract の詳細は [progress-event-contract.md](../progress-event-contract.md) を参照）。

phase の index / total / label は各スキルのワークフロー構成に合わせて読み替えること。

---

```markdown
## 進捗イベントの出力（squirrel-notifier 連携）

このワークフローの各 Phase を開始するとき、最初に以下の形式の 1 行を Bash ツールで stdout へ出力すること:

echo '@squirrel-progress {"schemaVersion":1,"phaseIndex":<0始まりの現在Phase番号>,"totalPhases":<総Phase数>,"phaseLabel":"<Phase名>","timestamp":"<現在時刻 ISO 8601>"}'

出力例（総 Phase 数 8 のワークフローで Phase 3「修正」を開始する場合）:

echo '@squirrel-progress {"schemaVersion":1,"phaseIndex":3,"totalPhases":8,"phaseLabel":"修正","timestamp":"2026-07-11T10:00:00Z"}'

ルール:
- 1 イベント = 1 行。JSON 内に改行を含めない
- 行頭マーカー `@squirrel-progress ` は正確に出力する（大文字小文字・スペースを変えない）
- 最終 Verdict が確定した時点の出力には "verdict" フィールドを含める（例: "verdict":"APPROVED"）
- 補足したい情報がある場合は "message" フィールドに短く含める
- この出力はベストエフォートでよい。出力に失敗してもワークフロー自体は継続する
```
