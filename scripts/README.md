# scripts フォルダ概要

## インストール関連
- `install.ps1`: アプリをタスクスケジューラに登録します。`-StartMinimized` でトレイ起動を指定可能、`-ExePath` で実行ファイルを明示できます。`-NonInteractive` は既存タスクを上書きせず、アプリを起動しない headless 検証用です。
- `uninstall.ps1`: タスクスケジューラ登録を解除します。`-KeepSettings` または `-NonInteractive` を付けると設定ファイルを残します。`-NonInteractive` は実行中のアプリを停止せず失敗します。
- `create-shortcuts.ps1`: スタートメニュー/デスクトップにショートカットを作成します（`-Tray` でトレイ起動用、`-Desktop` でデスクトップにも配置）。

## 開発用フック
- `scripts/hooks/pre-commit-format.ps1`: コミット前フォーマットチェック。
- `scripts/hooks/pre-commit-build.ps1`: コミット前ビルドチェック。
- `scripts/hooks/pre-commit-test.ps1`: プッシュ前テスト（カバレッジ閾値を含む）。

## その他
- `scripts/setup-dev.ps1`: 初回セットアップ（pre-commit インストール、NuGet 復元など）。
- `Assert-CiChecksPassed.ps1`: 指定コミットで CI の必須ジョブ（GitHub Actions の check run）が成功していることを確認します。tag push では `ci.yml` が起動しないため、`release.yml` の `verify-ci` が publish 前に使います。

### 補足: インストーラ用バンドル
リリースワークフローで `publish/<platform>` の実行ファイルと `install.ps1` / `uninstall.ps1` を同梱した Zip (`SquirrelNotifier-Setup-<version>-x64.zip`) を生成し、リリースアセットに含めます。README にダウンロード方法を追記してください。
