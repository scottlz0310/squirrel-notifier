# scripts フォルダ概要

## インストール関連
- `install.ps1`: アプリをタスクスケジューラに登録します。`-StartMinimized` でトレイ起動を指定可能、`-ExePath` で実行ファイルを明示できます。`-NonInteractive` は既存タスクを上書きせず、アプリを起動しない headless 検証用です。
- `uninstall.ps1`: タスクスケジューラ登録を解除します。`-KeepSettings` または `-NonInteractive` を付けると設定ファイルを残します。`-NonInteractive` は実行中のアプリを停止せず失敗します。
- `create-shortcuts.ps1`: スタートメニュー/デスクトップにショートカットを作成します（`-Tray` でトレイ起動用、`-Desktop` でデスクトップにも配置）。

## 開発用フック
- `scripts/hooks/pre-commit-format.ps1`: コミット前フォーマットチェック。
- `scripts/hooks/pre-commit-build.ps1`: コミット前ビルドチェック。
- `scripts/hooks/pre-commit-test.ps1`: プッシュ前テスト（カバレッジ閾値を含む）。

## desktop E2E runner（AWS）
`scripts/aws/` は、desktop E2E の self-hosted Windows runner を EC2 起動だけで online にするための bootstrap です。手順と背景は [`docs/windows-integration-e2e.md`](../docs/windows-integration-e2e.md) の「runner ホストの bootstrap」を参照してください。

- `Initialize-DesktopRunnerInstanceProfile.ps1`: SSM 管理に必要な IAM role / instance profile を作成し、対象 instance へ関連付けます（開発機から実行）。
- `Invoke-DesktopRunnerBootstrap.ps1`: bootstrap を SSM Run Command で投入します（開発機から実行）。
- `Setup-DesktopRunnerHost.ps1`: 自動ログオン、ログオン時の runner 起動、対話セッション維持設定を適用します（instance 内で実行）。
- `DesktopRunnerHost.psm1`: 適用内容を組み立てます。契約は `DesktopRunnerHost.Tests.ps1` が固定します。

これらは SSM Run Command の既定 shell である Windows PowerShell 5.1 で実行されるため、UTF-8 BOM 付きで保存します（`.editorconfig` に指定）。

## その他
- `scripts/setup-dev.ps1`: 初回セットアップ（pre-commit インストール、NuGet 復元など）。

### 補足: インストーラ用バンドル
リリースワークフローで `publish/<platform>` の実行ファイルと `install.ps1` / `uninstall.ps1` を同梱した Zip (`SquirrelNotifier-Setup-<version>-x64.zip`) を生成し、リリースアセットに含めます。README にダウンロード方法を追記してください。
