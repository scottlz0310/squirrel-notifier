# WinUI 3 版 Squirrel Notifier (preview)

PowerShell 版はそのまま維持しつつ、WinUI 3 / Windows App SDK ベースの軽量常駐アプリを開始しました。現状は最小骨格です。

## どこにあるか
- ソース: `winui3/SquirrelNotifier.WinUI3/`
- ソリューション: `winui3/SquirrelNotifier.WinUI3.sln`

## 依存/前提
- Windows 10 20H2 (19042) 以降（推奨: Windows 11）
- .NET 8 SDK
- Visual Studio 2022 17.10+ か、`dotnet` CLI
- Windows App SDK 1.6 (NuGet で取得)。App Notifications を使うため、**パッケージ化（MSIX or sparse package）での実行**を推奨。

## 現状の実装概要
- `KernelWatcherService`: `wsl.exe uname -r` で現在のカーネルを取得し、GitHub API から最新タグを取得。簡易比較後、更新があればトースト通知を投げる。ステータスは `LoggingService` 経由でローカルログにも書き出し（1MBでローテーション）。
- `NotificationService`: Windows App SDK の AppNotification を利用。パッケージ化して起動しない場合、通知が落ちる可能性があるので注意。
- `LoggingService`: `%LOCALAPPDATA%/SquirrelNotifier/logs/winui3.log` に追記し、UI に直近ログを流す。
- `MainWindow`: ステータス表示、直近ログリスト、`Check now` / `Open log folder` / `Exit` ボタン。バックグラウンドは `PeriodicTimer` で 2h 間隔チェック。

## ビルド & 実行 (CLI)
```pwsh
# ルートで実行
cd winui3
# 復元
dotnet restore SquirrelNotifier.WinUI3.sln
# ビルド
dotnet build SquirrelNotifier.WinUI3.sln -c Release
# 起動（アンパッケージのため通知は限定的）
dotnet run --project SquirrelNotifier.WinUI3/SquirrelNotifier.WinUI3.csproj
```
> AppNotification のフル機能を使うには、Visual Studio から「パッケージの発行/配布」または `makeappx` 等で MSIX 化して実行してください。

## 次にやること（例）
1. MSIX パッケージング（署名付き）を用意し、AppNotification が確実に出るようにする。
2. タスクトレイアイコン + コンテキストメニュー（今すぐチェック、ログフォルダ、終了）。
3. 設定 UI（間隔、リポジトリ override、ログパス）。
4. 失敗時の指数バックオフとログローテーション。
5. CI で `dotnet build` を回し、テンプレ署名用のテスト証明書を生成するステップを追加。

## 既知の制限
- アプリ通知はパッケージ ID が無いと出ない場合があります（CLI 実行時は特に）。パッケージ化を前提にしてください。
- 設定/永続化は未実装（デフォルト2h）。
- タスクトレイ常駐やスタートアップ登録は未対応。
- 署名・配布チャネル（MSIX/Store/winget/choco）は未整備。
