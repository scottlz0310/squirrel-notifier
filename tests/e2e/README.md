# Squirrel Notifier Phase 1 headless E2E

このディレクトリは、WinUI の起動や xUnit の in-process test では検証できない
cross-process E2E を管理します。Issue #307 では launcher の session resume 境界だけを
対象にし、実 CLI、GitHub、gateway、認証、ブラウザ、実 PR は使用しません。

## 実行

全 scenario は次のコマンドで、CI と同じ entrypoint から実行できます。

    pwsh -File .\tests\e2e\scripts\Invoke-E2E.ps1 -Phase Headless

1 scenario だけを実行する場合:

    pwsh -File .\tests\e2e\scripts\Invoke-E2E.ps1 -Phase Headless -Scenario session-resume-client-assigned -ArtifactsDirectory .\artifacts\e2e-local

runner は最初に dummy launcher と headless runner を build し、各 scenario を retry なしで
実行します。scenario ごとに RUNNER_TEMP 配下の専用 root を作成し、成功・失敗にかかわらず
cleanup と artifact 検査を行います。cleanup が失敗した場合は残りの scenario を実行しません。

## Scenario

| ID | 境界 |
|---|---|
| session-resume-client-assigned | ClientAssigned の新規 UUID、保存、resume |
| session-resume-expired | TTL 切れの検出、理由ログ、新規 UUID |
| session-resume-working-directory-mismatch | checkout mapping 変更後の新規 UUID |
| session-resume-failed-resume | resume 非ゼロ終了、entry 破棄、次回新規起動 |
| session-resume-disabled | 既存 command line の維持と store 未作成 |
| session-resume-parsed-output | Codex JSONL からの ParsedFromOutput と resume |

fixture の dummy executable は codex.exe という名前で build されます。ParsedFromOutput
scenario では一時 PATH から codex として解決し、それ以外では絶対パスで起動します。
dummy は引数、working directory、標準入力のリダイレクト状態、出力形式、終了コードを
専用 root 内の JSONL へ記録します。

## Artifact と cleanup

artifact は ArtifactsDirectory の headless 配下に run ID と scenario ID を付けて保存します。
raw invocation record と sessions.json は artifact にコピーしません。command line、settings、
log は session ID をサニタイズしてから出力し、Test-E2EArtifacts.ps1 が full UUID と token
pattern を検査します。

明示的に cleanup を再実行する場合は、Invoke-E2E.ps1 が表示した scenario root を指定します。

    pwsh -File .\tests\e2e\scripts\Invoke-E2ECleanup.ps1 -RunRoot <scenario-root> -ArtifactsDirectory .\artifacts\e2e-local\headless\<run-id>\<scenario-id>

この Phase 1 の launcher 境界は #307 の最小スコープです。配布物、subscriber、gateway、
認証、MSI、Task Scheduler を跨ぐ E2E は #221〜#223 の契約に従って追加します。
