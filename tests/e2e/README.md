# Squirrel Notifier Phase 1 headless E2E

このディレクトリは、WinUI の起動や xUnit の in-process test では検証できない
cross-process E2E を管理します。Issue #307 では launcher の session resume 境界を、
Issue #222 では subscriber、gateway、認証の契約を、Issue #223 では enqueue から
通知モデル・dummy launcher までのプロセス境界を検証します。実 GitHub、実 gateway、
実 OAuth provider、実ブラウザ、実 PR は使用しません。

## 実行

全 scenario は次のコマンドで、CI と同じ entrypoint から実行できます。

    pwsh -File .\tests\e2e\scripts\Invoke-E2E.ps1 -Phase Headless

1 scenario だけを実行する場合:

    pwsh -File .\tests\e2e\scripts\Invoke-E2E.ps1 -Phase Headless -Scenario session-resume-client-assigned -ArtifactsDirectory .\artifacts\e2e-local

runner は最初に dummy launcher、dummy subscriber、headless runner を build し、各 scenario を
retry なしで実行します。scenario ごとに RUNNER_TEMP 配下の専用 root を作成し、成功・失敗に
かかわらず cleanup と artifact 検査を行います。cleanup が失敗した場合は残りの scenario を
実行しません。

## Scenario

| ID | 境界 |
|---|---|
| session-resume-client-assigned | ClientAssigned の新規 UUID、保存、resume |
| session-resume-expired | TTL 切れの検出、理由ログ、新規 UUID |
| session-resume-working-directory-mismatch | checkout mapping 変更後の新規 UUID |
| session-resume-failed-resume | resume 非ゼロ終了、entry 破棄、次回新規起動 |
| session-resume-disabled | 既存 command line の維持と store 未作成 |
| session-resume-parsed-output | Codex JSONL からの ParsedFromOutput と resume |
| subscriber-gateway-contract | 実 subscriber の引数、success、404、401、500、接続拒否の契約 |
| gateway-auth-flow | `.cmd` / `.bat` 解決、device flow、token cache 後の再購読 |
| review-event-flow | 購読開始、`enqueue_review`、InitialText / FinalText の重複排除、通知モデル、dummy launcher |
| distribution-install | 実 publish payload、MSI、setup ZIP の build、silent install / uninstall、version、cleanup |

fixture の dummy executable は codex.exe という名前で build されます。ParsedFromOutput
scenario では一時 PATH から codex として解決し、それ以外では絶対パスで起動します。
dummy は引数、working directory、標準入力のリダイレクト状態、出力形式、終了コードを
専用 root 内の JSONL へ記録します。

dummy subscriber は `mcp-resource-subscriber.exe` として build され、loopback の fake Gateway
だけへ接続します。`gateway-auth-flow` では実製品の `McpLoginService` と
`McpSubscriptionService` がこのプロセスを起動し、`review-event-flow` では
`ReviewRegistrationService` / `EnqueueReviewService` も同じ実プロセス境界を通ります。
token marker は artifact へ出力しません。

## Artifact と cleanup

artifact は ArtifactsDirectory の headless 配下に run ID と scenario ID を付けて保存します。
raw invocation record と sessions.json は artifact にコピーしません。command line、settings、
log は session ID をサニタイズしてから出力し、Test-E2EArtifacts.ps1 が full UUID と token
pattern を検査します。

明示的に cleanup を再実行する場合は、Invoke-E2E.ps1 が表示した scenario root を指定します。

    pwsh -File .\tests\e2e\scripts\Invoke-E2ECleanup.ps1 -RunRoot <scenario-root> -ArtifactsDirectory .\artifacts\e2e-local\headless\<run-id>\<scenario-id>

この Phase 1 の launcher、subscriber、gateway、認証、enqueue から通知モデルまでの
headless 境界は #307、#222、#223 のスコープです。`distribution-install` は #221 の
契約に従い、release workflow と同じ publish / WiX 経路で配布物を検証します。
既存インストール、製品登録、Task Scheduler タスク、実行中プロセスを開始前に検出した
場合は変更せず失敗します。
