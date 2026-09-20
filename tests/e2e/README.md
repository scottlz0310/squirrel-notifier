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

## Phase 2 実デスクトップ E2E

Phase 2 の reusable workflow は `.github/workflows/desktop-e2e.yml` です。定期実行は行わず、
実装時の検証は `main` ref の `.github/workflows/desktop-e2e-dispatch.yml` を
`workflow_dispatch` で実行し、入力 `target_ref` に検証対象の branch/tag を指定します。
release workflow は同じ reusable workflow を release 前 gate として呼び出します。

EC2 runner の起動・停止は workflow の GitHub-hosted job が担当します。`prepare-runner` と
`desktop-e2e`、`cleanup-runner` は `desktop-e2e` environment を宣言します。この environment
は trusted ref として `main` branch と `v*` tag だけを許可します。手動実行は `main` ref の
trusted dispatcher から開始するため、feature branch の workflow 定義を実行せずに、その branch
のコードだけを `target_ref` として検証できます。target ref の package build は GitHub-hosted
runner で E2E secret 無しに行い、protected environment の desktop job には artifact だけを
渡します。desktop job は trusted ref の harness を使用し、target package の MSI custom action
と製品プロセスへ E2E 用の値を継承させません。次の environment variables を事前に設定してください。

- `DESKTOP_E2E_AWS_ROLE_ARN`: GitHub OIDC を信頼する最小権限 IAM role の ARN
- `DESKTOP_E2E_AWS_REGION`: EC2 のリージョン
- `DESKTOP_E2E_AWS_INSTANCE_ID`: 対象 EC2 instance ID
- `DESKTOP_E2E_RUNNER_NAME`: self-hosted runner の登録名

次の GitHub App の設定も必要です。Appは `squirrel-notifier` だけへインストールし、Repository
`Administration: Read` だけを許可してください。workflowは実行ごとに短期の installation token を
生成するため、installation token自体を保存しません。

- `DESKTOP_E2E_GITHUB_APP_ID`: GitHub AppのApp ID（environment variable、秘密ではない）
- `DESKTOP_E2E_GITHUB_APP_PRIVATE_KEY`: GitHub Appの秘密鍵（`desktop-e2e` environment secret）

秘密鍵はworkflowログへ出力しません。

IAM role には対象 instance に対する `DescribeInstances`、`DescribeInstanceStatus`、
`StartInstances`、`StopInstances` だけを許可し、長期 AWS access key は使用しません。
workflow 開始前から instance が稼働していた場合は、別の運用主体の状態を保護するため停止しません。
停止中の instance をこの workflow が起動した場合だけ、E2E の成功・失敗・キャンセル後に停止します。
同一 instance の同時利用は `concurrency` で直列化します。

現段階では runner の Windows サービス登録、依存ツールの導入、AMI / User Data の生成は AWS 側の
一回限りのセットアップとして扱います。runner が `squirrel-notifier-desktop` label 付きで
online にならない場合、E2E job を開始せずに失敗します。

手動実行では `DesktopSmoke` を選ぶと、専用の対話ログオン済み Windows runner 上で次を検証します。

- MSI の install、製品 version、WinUI main window の launch
- `AutomationProperties.AutomationId` による主要 UI 要素の検出
- main window の screenshot
- MSI uninstall、process、Task Scheduler、registry、install directory の残留確認

`DesktopFull` は `DESKTOP_E2E_GATEWAY_URL`、`DESKTOP_E2E_RESOURCE_URIS`、component manifest、
外部 stack の停止・復旧、device flow、queue、通知、sleep/resume を実行する
`DESKTOP_E2E_FULL_DRIVER` を runner image に登録した場合だけ使用します。未設定の場合は
成功扱いにせず、runner 契約違反として失敗します。token、device code、settings の内容は
artifact へ出力しません。
待機時間は `tests/e2e/scenarios/desktop-*.json` の `timeoutSeconds` を使用します。
DesktopFull の component manifest は `requiredComponents` の全 component に固定 version と
`sha256:` digest を要求し、driver は `full-driver-result.json` に必須 step の成功結果を出力します。

ローカルの対話 desktop で MSI を直接検証する場合:

    pwsh -File .\tests\e2e\scripts\Invoke-DesktopE2E.ps1 `
      -MsiPath .\release-output\SquirrelNotifier-Setup-0.12.0-x64.msi `
      -Scenario DesktopSmoke `
      -ArtifactsDirectory .\artifacts\e2e-local\desktop

runner の EBS 容量を決める前に、次を実行して測定結果を保存します。

    pwsh -File .\tests\e2e\scripts\Measure-DesktopE2EStorage.ps1 `
      -OutputPath .\artifacts\e2e-local\desktop\storage.json
