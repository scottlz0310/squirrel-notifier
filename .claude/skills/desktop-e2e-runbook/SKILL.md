---
name: desktop-e2e-runbook
description: リリース前に Squirrel Notifier の実デスクトップ E2E ランブック（tests/e2e/agent-runbooks）を実行し、結果を results/v<version>.json に記録する。リリース準備 PR のブランチで、対象リリースを実装していないセッションが使う。
---

# desktop-e2e-runbook

この skill は入口だけを持ちます。手順・合否の基準・記録形式はすべて repo の Markdown にあり、ここには書きません。

1. `tests/e2e/agent-runbooks/README.md` を読み、原則・ツール・操作の注意に従う
2. このセッションが対象リリースの実装や PR 作成をしていないことを確かめる。していれば実行せず、別セッションでの実行を利用者に依頼して終える（`implementerSession` を `false` と偽らない）
3. 作業ツリーがクリーンで、HEAD がリリース準備 PR のブランチの先頭であることを確かめる
4. `tests/e2e/agent-runbooks/scenarios/` の `required: true` のシナリオを順に実行する。引数で シナリオ ID が渡されたら、そのシナリオだけを実行し、既存の結果ファイルの該当エントリだけを更新する
5. `results/v<version>.json` を README の形式で書き、結果ファイルだけをコミットする。push は利用者の指示に従う
6. 利用者に、シナリオごとの合否・所要時間・flaky の観測を報告する
