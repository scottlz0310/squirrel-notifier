# Changelog

All notable changes to this project will be documented in this file.

## [Unreleased]

### Fixed
- `install.ps1` のパス解決を修正し、`publish/` 配下の自己完結型成果物のみを検索・登録するよう変更（`bin/` 直下の非自己完結型 EXE が誤って選択される問題を解消）
- MCP 購読接続の一時的なエラー（サーバー未起動や認証切れによる `fetch failed` 等）発生時に、即座に `Error` 状態で停止していた問題を修正。指数バックオフ（初期 1 秒、最大 32 秒、最大 5 回）による自動リトライを導入し、一時的な障害からの自動回復を可能にした

## [0.1.0] - 2026-06-14

### Added
- 起動時自動チェック等において、特定のバージョンをスキップするための「スキップされたバージョン」保存機能 (`LastSkippedVersion`) を `AppSettings`/`SettingsService` に追加
- 自動更新通知のダイアログに「このバージョンをスキップ」ボタンを追加し、手動チェックと自動チェックの切り分け・スキップ動作の永続化をサポート
- `AutoUpdateService.CheckForUpdatesAsync` に、一時的なネットワーク障害に対応するための自動リトライ機能（最大3回、指数バックオフ）および個別リクエストのタイムアウト処理（5秒）を導入
- ユーザー起点の安全な外部 launcher (`ReviewLauncherService`) を実装。通知またはアプリ UI から、設定された外部レビューアクションを安全に起動可能に
- 安全な引数置換機構 (`LauncherArgumentBuilder`) を導入。プレースホルダー (`{owner}`, `{repo}`, `{prNumber}`, `{prUrl}`) を用いて、シェルインジェクションを完全に防ぎつつ安全に引数を構築
- 設定ウィンドウに外部 launcher 用の設定項目 (Launcher Path, Launcher Args, Launcher Timeout) を追加
- トースト通知に「レビューを起動」ボタンを追加し、通知から即座にレビュー実行をトリガー可能に
- レビュー実行中のステータス表示、ユーザーからのキャンセル、実行完了後の詳細な結果 (終了コード、stdout/stderr) を表示するダイアログ UI を MainWindow に追加
- MCP 購読ブリッジ機構 (`McpSubscriptionService`) を新設。`mcp-resource-subscriber` と連携し、外部 MCP リソースからのレビュー更新イベントを取得可能に
- `McpSubscriptionService` 用の動作確認 UI を設定ウィンドウに追加（コマンド実行パス、引数、URL、リソース URI、タイムアウト of 編集に対応）
- `mcp-resource-subscriber` から受信した JSON 形式のレビューイベント（`ReviewEvent`）をパースするデシリアライズ・検証機構を導入
- レビューイベント専用の Windows 通知を構築（通知から検証済み PR URL をブラウザで開くアクション、およびアプリを開くアクションに対応）
- メモリ内の重複排除キャッシュ（最大100件）による、同一 `eventId` を持つレビューイベントの重複通知防止機能を実装
- メイン画面 UI に直近のレビューイベント（Recent review events）の履歴リストを追加し、一覧から直接 PR を開けるボタンを配置

### Removed
- 旧 WSL カーネル監視機能 (`KernelWatcherService` および関連 UI / ロジック) を完全に廃止

### Changed
- `ReviewEventParser.Parse` の戻り値を `List<ReviewEvent>` に変更し、`ReviewCandidate` 配列内の全新着イベントをパース・ループ処理するよう拡張
- `ReviewCandidate` ペイロードの変更（`url` 等の欠落）に合わせ、`Owner`/`Repo`/`PrNumber` からの PR URL 自動構築と、`queuedAt` 等による `eventId` 自動生成を追加、および `sourceCommentId` の型を `long?` に修正しデシリアライズエラーを防止
- 重複排除とトースト通知の安定動作を検証するユニットテストを `ReviewEventParserTests` 等に新設・追従
- アプリケーション、ソリューション、プロジェクト、アセンブリ名、および C# の名前空間を `SquirrelNotifier` へ統一
- 設定保存先ディレクトリを `%LocalAppData%\WSLKernelWatcher` から `%LocalAppData%\SquirrelNotifier` に変更
- ウィンドウタイトル、トレイアイコンのツールチップ、メニュー、アプリ表示名を `Squirrel Notifier` へ変更
- 自動更新用の GitHub リポジトリ URL および User-Agent を `squirrel-notifier` に追従
- タスクスケジューラのタスク登録（`install.ps1`）を `Squirrel Notifier` に変更し、旧タスク検出時に手動移行手順を促すよう改善
- タスクスケジューラのタスク登録スクリプト (`install.ps1`) の説明文を MCP リソース監視のものに更新
- アプリケーションのアイコンファイルを `squirrel-notifier.ico` (新アセット `assets/squirrel-notifier.png` を基に生成) に差し替え
- 開発用ツールセットの Python プロジェクト名を `squirrel-notifier-devtools` に変更
- トレイ通知のイベント発生時、レビュー URL 開くボタンを（今回のスコープ外のため）一旦削除

## [3.1.0] - 2025-12-15

### Added
- GitHub Actions リリース成果物に WinUI 3 アプリのセットアップ Zip（install/uninstall/create-shortcuts 同梱）を追加

### Fixed
- `dotnet publish` 生成物に PRI が含まれず起動できない問題を解消し、配布物でも起動を確認

### Changed
- アプリバージョンを 3.1.0 に更新し、配布物のバージョン表記を統一

## [3.0.0] - 2025-12-15

### Changed
- WinUI 3 アプリを .NET 10 + Windows App SDK 1.8 に更新し、サポート OS を Windows 11 24H2 以降に整理
- ビルド/テスト/ドキュメントを 3.0.0 向けに整備し、手動起動パスを最新版に更新
- Python 実装を廃止し、WinUI 3 ベースのアプリに一本化

### Technical
- CI パイプラインを .NET 10 / Windows App SDK 1.8 に追随
- フォールバックとして PowerShell スクリプト版を引き続き同梱

## [2.1.1] - 2025-01-20

### Fixed
- コード品質チェック100%通過対応
- テストの安定性向上（ファイルシステム依存排除）
- バージョン整合性の完全統一

### Technical
- ruff設定最適化
- DockerNotifierテストのモック化改善
- 統合テストの通知システムモック化

## [2.1.0] - 2025-10-20

### Added
- GitHub Personal Access Token対応 (GITHUB_PERSONAL_ACCESS_TOKEN)
- 動的ユーザー名・パス対応によるポータブル化
- バージョン変更通知テスト追加
- 完全フローテスト追加
- インタラクティブテスト機能

### Fixed
- systemd監視システムの競合状態解決
- ファイル削除エラーの抑制
- GitHub APIレート制限問題の解決

### Changed
- レート制限: 60 → 5000リクエスト/時間
- テスト成功率: 83.3% → 100.0%
- systemdサービスのテンプレート化

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.0.0] - 2025-01-19

### Added
- 🐳 **Docker常駐アーキテクチャ**: 完全新設計による軽量化
  - Linuxコンテナで24/7監視
  - WSL経由でWindows Toast通知
  - 環境変数による簡単設定
  - 自動再起動機能
- 🔧 **操作系テストスイート**: 包括的な動作確認
  - Dockerビルド・起動確認
  - GitHub API接続テスト
  - WSL経由通知テスト（WSL環境自動検知）
  - エラーハンドリング確認
- 🌐 **WSL環境自動検知**: `/proc/version`からMicrosoft/WSL検出
- ⚙️ **環境変数設定**: docker-compose.ymlで全設定管理

### Changed
- 🏗️ **破壊的変更**: タスクトレイ常駐からDocker常駐に完全移行
- 📦 **依存関係最小化**: Docker環境に最適化
- 🔄 **非同期処理**: リソース効率的な実装

### Removed
- ❌ **タスクトレイ機能**: Docker常駐により不要
- ❌ **config.toml**: 環境変数に統一
- ❌ **Windows専用依存**: Linuxコンテナで動作

### Technical
- Docker + docker-compose対応
- WSL経由PowerShell実行
- 環境変数による設定管理
- 操作系テスト完備

## [1.2.0] - 2025-01-10

### Added
- 🔧 **Pre-commitフック強化**: 開発品質向上のための包括的なフック設定
  - ruff check --fix: 自動コード修正
  - ruff format: コードフォーマット統一
  - mypy: 型チェック強化
  - pytest: テスト実行の自動化
- 🏗️ **uv対応**: 最新のPythonパッケージマネージャーに完全対応
  - dependency-groupsによる開発依存関係管理
  - 高速な依存関係解決とインストール
  - CI/CD環境での最適化
- 📦 **自動インストールスクリプト**: install.ps1による簡単セットアップ
  - uv/pipx の自動検出とインストール
  - 仮想環境の自動作成
  - config.toml の自動生成
  - カスタムインストールパス対応

### Changed
- 📦 **依存関係管理の現代化**
  - requirements.txtからpyproject.toml + dependency-groupsに移行
  - 開発用依存関係の明確な分離
- 🔍 **コード品質チェックの強化**
  - MyPy型チェックの厳格化
  - Ruffによる包括的なリント設定
  - pytest-timeoutによるハングアップ対策
- 🚀 **CI/CD最適化**
  - GitHub ActionsでのWindows環境最適化
  - プラグイン競合問題の解決
  - ローカル・CI環境の設定統一

### Fixed
- 🐛 **Windows環境での問題解決**
  - pytest-timeoutプラグインの重複登録エラー修正
  - Windows-Toastsインポートエラー処理改善
  - パス処理のWindows互換性向上
- 🔧 **設定ファイルの整合性**
  - Pre-commit設定とCI設定の完全同期
  - プラグイン明示指定の最適化

### Technical
- Python 3.9-3.13 対応継続
- uv 0.8.22+ 対応
- 新しい開発依存関係: pytest-timeout, pre-commit
- Windows Server 2025 CI環境対応

## [1.1.0] - 2025-01-05

### Added
- 🆕 **ワンショットモード**: 一度だけチェックして終了する新しい実行モード
  - CI/CDパイプラインやスケジュールタスクでの利用に最適
  - `config.toml`で`execution_mode = "oneshot"`に設定
  - 常駐せずに即座に終了するため、軽量で効率的
- 階層構造対応の設定ファイル形式
  - `[general]`, `[notification]`, `[logging]`セクションに分離
  - 既存のフラット構造との互換性も維持
- 包括的なテストカバレッジ（86.29%達成）
  - ワンショットモード専用テストスイート
  - エンドツーエンド統合テスト
  - エラーハンドリングテスト

### Changed
- 設定ファイル構造の改善（階層化）
- コード品質の向上（Ruff lint 100%通過）
- 型アノテーションの完全対応
- 例外処理の改善（適切なfrom句の追加）

### Fixed
- 設定ファイル読み込み時の階層構造対応
- テストケースの安定性向上
- 型チェックエラーの解消

### Technical
- Python 3.9+ 対応
- 新しい依存関係: types-requests, types-toml
- CI/CD対応の完了（全リントチェック通過）

## [1.0.0] - 2024-12-XX

### Added
- 初回リリース
- GitHub APIを使用したWSL2カーネルリリース監視
- プレリリース除外機能
- Windowsトースト通知
- タスクトレイ常駐機能
- 設定ファイル管理
- 包括的なログ機能
- レート制限対応
- リトライ機能

[Unreleased]: https://github.com/scottlz0310/squirrel-notifier/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/scottlz0310/squirrel-notifier/releases/tag/v0.1.0
[3.1.0]: https://github.com/scottlz0310/squirrel-notifier/compare/v3.0.0...v3.1.0
[3.0.0]: https://github.com/scottlz0310/WSL-kernel-watcher/compare/v2.1.1...v3.0.0
[2.1.1]: https://github.com/scottlz0310/WSL-kernel-watcher/compare/v2.1.0...v2.1.1
[2.1.0]: https://github.com/scottlz0310/WSL-kernel-watcher/compare/v2.0.0...v2.1.0
[2.0.0]: https://github.com/scottlz0310/WSL-kernel-watcher/compare/v1.2.0...v2.0.0
[1.2.0]: https://github.com/scottlz0310/WSL-kernel-watcher/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/scottlz0310/WSL-kernel-watcher/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/scottlz0310/WSL-kernel-watcher/releases/tag/v1.0.0
