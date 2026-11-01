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
| distribution-install | 実 publish payload、MSI、setup ZIP の build、silent install / uninstall、major upgrade、全 payload の SHA256、WinUI 初回起動、cleanup |

fixture の dummy executable は codex.exe という名前で build されます。ParsedFromOutput
scenario では一時 PATH から codex として解決し、それ以外では絶対パスで起動します。
dummy は引数、working directory、標準入力のリダイレクト状態、出力形式、終了コードを
専用 root 内の JSONL へ記録します。

dummy subscriber は `resource-bridge-cli.exe` として build され、loopback の fake Gateway
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

### MSI 更新の回帰検証（#525）

`distribution-install` は `Invoke-MsiUpgradeE2E.ps1` で製品名・UpgradeCode・配置先・
レジストリ・ショートカット・タスク名を一意に変えた MSI を生成します。WiX 定義と
依存バージョンは本体と共通です。旧製品の2DLLだけを高い FileVersion の fixture にして、
強制配置を外した対照 MSI で欠落が再現することを確認します。修正版では高 FileVersion・
通常更新・同版再ビルドの置換・同じ MSI の再実行後に、全 payload の存在と SHA256、
設定・専用データ・カスタムタスクの保持を照合します。WinUI の実起動後に生成される
新鮮な `review-status.json` とプロセスの継続を検証し、起動を省略して成功にはしません。
この確認はリリース時の独立した実デスクトップランブックを置き換えません。

更新部分だけを既存インストールがある開発 PC で実行する場合:

    pwsh -NoProfile -File tests/e2e/scripts/Invoke-MsiUpgradeE2E.ps1 -PublishDirectory <実publish出力> -RunRoot ./artifacts/msi-upgrade -ArtifactsDirectory ./artifacts/msi-upgrade-evidence

実アプリのデータは変更せず、試験用アプリは `SQUIRREL_NOTIFIER_DATA_ROOT` を専用 root に
指定して起動します。subscriber は存在しない専用パスを指定し、実 gateway・queue・資格情報へ
接続しません。生成した製品だけをアンインストールし、実設定の hash と隔離製品の残留を
検査します。試験用 DLL は旧インストール状態の構築にだけ使い、起動するのは実 payload です。
`msi-upgrade.json` とサニタイズ済み `msi-upgrade.log` は成功時も CI artifact に保存します。

## 実デスクトップの確認

self-hosted Windows（AWS EC2）で実デスクトップを動かす Phase 2 は撤去しました（#426 / #429）。
実際の WinUI を含む確認は、リリース前に AI エージェントが [agent-runbooks](agent-runbooks/README.md) に沿って行います（#433）。
ランブックの fake Gateway には、headless runner の `serve-gateway` モード（loopback で待ち受け、`--endpoint-file` に URL を書く）を使います。
