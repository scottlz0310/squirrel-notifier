# Squirrel Notifier

<img src="assets/squirrel-notifier.png" width="128" alt="Squirrel Notifier" />

[![CI](https://github.com/YOUR_USERNAME/squirrel-notifier/actions/workflows/ci.yml/badge.svg)](https://github.com/YOUR_USERNAME/squirrel-notifier/actions/workflows/ci.yml)
[![Release](https://github.com/YOUR_USERNAME/squirrel-notifier/actions/workflows/release.yml/badge.svg)](https://github.com/YOUR_USERNAME/squirrel-notifier/actions/workflows/release.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![codecov](https://codecov.io/gh/YOUR_USERNAME/squirrel-notifier/branch/main/graph/badge.svg)](https://codecov.io/gh/YOUR_USERNAME/squirrel-notifier)

Squirrel Notifierは、mcp-gateway を経由して MCP resource update（レビュー更新イベントなど）を監視し、更新があった場合に通知を行う常駐型軽量アプリケーションです（安定版: 0.1.0）。

## 機能

- mcp-gateway を経由したレビュー更新イベントの監視（mcp-resource-subscriberを使用）
- 更新時のトースト通知
- ログファイルへの記録
- システムトレイ常駐
- コマンドパス、引数、Gateway URL、Resource URI、タイムアウトのカスタマイズ

## 前提ツール

Squirrel Notifier の購読機能は、以下の外部ツールと連携して動作します。

### mcp-resource-subscriber

MCP リソースの購読・イベント受信を担う外部 CLI ツールです。Squirrel Notifier には同梱されていないため、**別途インストールが必要**です。

- **リポジトリ**: [scottlz0310/mcp-resource-subscriber](https://github.com/scottlz0310/mcp-resource-subscriber)
- **インストール**: リリースページからバイナリを取得し、PATH の通ったディレクトリに配置してください。

インストール後、アプリの「Settings」→「Command Path」にコマンド名（PATH に通っていれば `mcp-resource-subscriber`）またはフルパスを設定してください。

### mcp-gateway

MCP リソースイベントを配信するゲートウェイサーバーです。reviewers forest エコシステムの一部として別途起動が必要です。

- アプリの「Settings」→「Gateway URL」にエンドポイントを設定してください（例: `http://localhost:3000`）
- 「Resource URI」には監視対象のリソース URI を設定してください（例: `queue://review/queue`）

## WinUI3版

### 必要環境

- Windows 11 24H2 (10.0.26100) 以降を推奨
- .NET 10 SDK
- Visual Studio 2026 (WinUI3ワークロード含む)
- Windows App SDK 1.8

### ビルド方法

#### Visual Studioから
1. `winui3/SquirrelNotifier.WinUI3.sln` をVisual Studioで開く
2. 構成を `Release` / `x64` に設定
3. ビルド → ソリューションのビルド (`Ctrl+Shift+B`)

#### コマンドラインから
```powershell
# .NET SDK（global.jsonに一致するバージョン）を使用
dotnet build winui3\SquirrelNotifier.WinUI3.sln -c Release -p:Platform=x64
```

> **注意:** .NET SDK 10.0.102（global.jsonで指定）が必要です。

### インストール（自動起動設定）

#### 配布物

リリースページ（例: https://github.com/scottlz0310/squirrel-notifier/releases/latest ）に以下の x64 向け配布物を公開しています（0.1.0 以降）：

- **MSI インストーラー**（`SquirrelNotifier-Setup-<version>-x64.msi`）: ウィザード形式のインストーラー。ダブルクリックで実行できます。
- **セットアップ Zip**（`SquirrelNotifier-Setup-<version>-x64.zip`）: 実行ファイルと `install.ps1` / `uninstall.ps1` / `create-shortcuts.ps1` を同梱。展開後に `install.ps1` を実行してください。

#### タスクスケジューラ登録
PowerShell でインストールスクリプトを実行して自動起動を設定できます:

```powershell
# 標準インストール（ログイン時にウィンドウ表示）
.\scripts\install.ps1

# トレイに最小化してインストール（推奨）
.\scripts\install.ps1 -StartMinimized

# カスタムパスを指定してインストール
.\scripts\install.ps1 -ExePath "C:\Path\To\SquirrelNotifier.WinUI3.exe" -StartMinimized
```

インストールすると、次回のログインから自動的に起動します。

#### ショートカット作成（必要に応じて）
展開したセットアップ Zip に含まれる `create-shortcuts.ps1` を使って、スタートメニューやデスクトップにショートカットを作成できます:

```powershell
# スタートメニューにショートカットを作成
.\scripts\create-shortcuts.ps1

# トレイ起動用ショートカットをスタートメニューとデスクトップに作成
.\scripts\create-shortcuts.ps1 -Tray -Desktop
```

### アンインストール

```powershell
# タスクと設定を削除
.\scripts\uninstall.ps1

# タスクのみ削除（設定は保持）
.\scripts\uninstall.ps1 -KeepSettings
```

### 手動起動

```powershell
# ビルド後の実行ファイルを起動
.\winui3\SquirrelNotifier.WinUI3\bin\x64\Release\net10.0-windows10.0.26100.0\SquirrelNotifier.WinUI3.exe

# トレイに最小化して起動
.\winui3\SquirrelNotifier.WinUI3\bin\x64\Release\net10.0-windows10.0.26100.0\SquirrelNotifier.WinUI3.exe --tray
```

または、Visual Studioから `F5` でデバッグ実行できます。

### 設定

アプリケーションを起動後、「Settings」セクションから以下の設定が可能です:

- **Command Path**: mcp-resource-subscriber の絶対パス（またはPATH内のコマンド名）
- **Arguments**: 必要な固定引数（スペース区切り）
- **Gateway URL**: mcp-gateway のエンドポイントURL（例: http://localhost:3000）
- **Resource URI**: 監視対象の MCP リソース URI（例: queue://review/queue）
- **Timeout (ms)**: タイムアウトミリ秒（1〜300,000 ms）

設定は `%LocalAppData%\SquirrelNotifier\settings.json` に保存されます。
なお、認証トークンなどの機密情報は設定ファイルには保存せず、環境変数 `MCP_PROBE_AUTH_TOKEN` から子プロセスへ渡されます。

## 開発

### 開発環境のセットアップ

初回のみ、開発環境をセットアップしてください:

```powershell
# Lefthook GitフックとNuGetパッケージをセットアップ
.\scripts\setup-dev.ps1
```

このスクリプトは以下を実行します:
- Lefthook Gitフックの設定（コミット時・プッシュ時のチェック）
- NuGetパッケージの復元

### Lefthook Gitフック

Lefthook により、以下のチェックが自動実行されます:

**コミット時**:
- コードフォーマットチェック（dotnet format）
- ビルドチェック（警告をエラー扱い）

**プッシュ時**:
- ユニットテスト実行
- コードカバレッジチェック（80%必須）

フックを一時的にスキップする場合:
```powershell
git commit --no-verify
git push --no-verify
```

### テストの実行

```powershell
dotnet test winui3/SquirrelNotifier.WinUI3.sln
```

### コード品質

プロジェクトには厳格な品質基準が設定されています:

#### 静的解析
- **StyleCop.Analyzers**: コードスタイルのチェック
- **Roslynator.Analyzers**: コード品質の分析
- **Microsoft.CodeAnalysis.NetAnalyzers**: .NET公式アナライザー
- **SecurityCodeScan**: セキュリティ脆弱性のスキャン
- **CodeQL**: GitHubセキュリティスキャン
- **TreatWarningsAsErrors**: すべての警告をエラーとして扱う

#### コードカバレッジ
- **目標カバレッジ**: 80%（行、分岐、メソッド）
- **Codecov統合**: PRごとにカバレッジレポート生成
- **自動生成コードを除外**: `*.g.cs`, `*.xaml.cs`

#### コードフォーマット
- **.editorconfig**: C#コーディング規約を強制
- **dotnet format**: CI時に自動フォーマットチェック

```powershell
# コードフォーマットの実行
dotnet format winui3/SquirrelNotifier.WinUI3.sln
```

## CI/CD

GitHub Actionsを使用した自動ビルド・テスト・リリースを実装しています:

- **CI**: プッシュ・PR時に自動ビルド・テスト・コード分析
  - ビルド（Release構成、警告をエラー扱い）
  - ユニットテスト（80%カバレッジ必須）
  - Lint（dotnet format）
  - セキュリティスキャン（CodeQL + SecurityCodeScan）
- **Release**: タグプッシュ時に自動リリース（x64, ARM64）
- **Renovate**: 依存関係の自動更新（週次）

## ライセンス

MIT License
