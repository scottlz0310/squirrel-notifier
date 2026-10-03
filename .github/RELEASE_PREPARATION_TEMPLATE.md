## リリース準備

release-automate が csproj のバージョン、CHANGELOG のバージョン節と比較リンクを更新した PR です。

- 別セッションで実デスクトップのランブックを実行し、`tests/e2e/agent-runbooks/results/` の結果だけを追加してください。
- CI と Runbook Guard が成功してから、独立レビューを受けてください。
- マージには利用者の明示許可が必要です。Squash merge のタイトルは `chore(release): vX.Y.Z` を維持してください。
- マージ後、Release workflow は `release` Environment の承認を待ちます。マージだけではタグ作成・公開は始まりません。
- Environment の承認後、同一コミットのタグ・draft 作成、成果物の添付と再取得による検証、finalize を行います。検証失敗時は draft のまま停止します。
