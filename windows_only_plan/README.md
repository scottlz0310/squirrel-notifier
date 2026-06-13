# 【歴史資料】Windows 完結型プロトタイプ: WSL Kernel Update Notifier

> [!NOTE]
> 本ディレクトリおよびスクリプトは、WSLカーネル監視時代の古いプロトタイプ（履歴資料）です。
> 現在の `Squirrel Notifier` プロジェクトの動作やビルドには関係ありません。

このディレクトリにある `windows-only-prototype.ps1` は、Windows 上で WSL2 カーネルの更新を監視し、通知するための単一 PowerShell スクリプトのプロトタイプです。

**重要:** これは古い実装であり、メインのアプリケーションロジックは WinUI3 アプリ (`winui3/SquirrelNotifier.WinUI3`) に移行しています。現在は履歴資料として保管しています。

## 機能概要
- WSL のカーネルバージョンを取得し、GitHub のリポジトリの最新リリースと比較します。
- 新しいバージョンが存在する場合は、Windows の通知でユーザーに知らせます（`BurntToast` モジュールがある場合はそれを使用）。
- タスクスケジューラへ登録して定期チェックを行う機能を備えています。
- 単体のテスト関数群（バージョン比較、WSL 接続、GitHub API、通知・ログ・タスク スケジューラ）を含みます。

## 依存関係
- Windows（PowerShell 実行環境）
- WSL（`wsl.exe` が PATH にあること）
- （任意）BurntToast モジュール（リッチ通知用。スクリプトのインストール処理で自動インストールする箇所があります）

## 利用方法
PowerShell を管理者で開く必要はありません（スケジュール登録はユーザー コンテキストで行われます）。

### インストール（タスク登録）
```powershell
.\windows-only-prototype.ps1 -Install
```
タスクスケジューラに「WSL Kernel Update Notifier」名で登録され、定期実行（デフォルトで2時間おき）されます。

### アンインストール（タスク削除）
```powershell
.\windows-only-prototype.ps1 -Uninstall
```

### 通常実行（1回チェック）
```powershell
.\windows-only-prototype.ps1
```

### テスト (通知や機能確認)
```powershell
.\windows-only-prototype.ps1 -Test         # 単一のテスト通知
.\windows-only-prototype.ps1 -RunTests     # 機能テストを実行
.\windows-only-prototype.ps1 -TestAll      # 機能テスト実行後、通常チェックも実行
```

## 設定
スクリプト冒頭で `$Config` ハッシュにデフォルト設定があります（リポジトリ、チェック間隔、ログパス、タスク名など）。必要なら `$ConfigPath` を使って外部 JSON から読み書きするよう拡張可能です。

## ロギング
- デフォルト ログパス: `"$env:TEMP\wsl-kernel-notifier.log"`。
- `Write-Log` 関数でログ出力を行います。

## テストと検証
スクリプト内部の `Invoke-AllTests` を実行すると、次の検証を実行します: ログ/WSL/GitHub API/通知/タスクスケジューラ/バージョン比較。

## ステータスと注意事項
- 現在はフォールバック実装です。新しい UI ベースの実装は `winui3/WSLKernelWatcher.WinUI3` にあります。
- 利用する場合、PowerShell のバージョンや WSL の可用性を確認してください。タスクの登録・実行に関してはユーザーのログオン状態に依存します。
- バージョン解析は正規表現で抽出しているため、タグ名の形式が変わると正しく検出できない可能性があります。

## 貢献とメンテナンス
フォールバックとして残しているため、大きな変更は計画に基づいて行ってください。フォーマット・ロギング・テストを含めて変更する場合は、`winui3/WSLKernelWatcher.WinUI3.Tests` のテスト方針に合わせて追加テストを作成してください。

## ライセンス
このリポジトリのライセンスに準拠します。必要に応じて `LICENSE` を確認してください。

## 参考リンク
- メインプロジェクト: /winui3/SquirrelNotifier.WinUI3
- 既存ドキュメント: /docs/WINDOWS_LIGHTWEIGHT_APP.md
