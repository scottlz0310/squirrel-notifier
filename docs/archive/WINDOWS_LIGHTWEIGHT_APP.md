# Windows専用・軽量常駐アプリ化 方針メモ

目的: `windows_only_plan/windows-only-prototype.ps1` をベースに、Python/Dockerを廃し、Windows専用で軽量かつ配布しやすい常駐アプリへ移行するための技術選択と実装方針を整理する。

## 前提とゴール
- 既存: 単一 PowerShell + タスクスケジューラで実用上問題なし。
- ゴール: Windows標準依存に近い形で常駐し、トースト通知・自動起動・ログを提供。メンテ容易で配布しやすいこと。

## 技術候補の比較（要約）
| 選択肢 | 概要 | 強み | 注意点 |
| --- | --- | --- | --- |
| A. 現行 PowerShell + タスクスケジューラ | `windows-only-prototype.ps1` をそのまま登録 | 依存最小・既に動作 | スクリプト配布/更新の仕組みが弱い |
| B. PowerShellモジュール化 + タスクスケジューラ（推奨・短期） | 機能を `.psm1` と設定ファイルに分離し、Install/Uninstall/Test スクリプトを用意 | 保守性・再利用性向上、配布形態を増やせる | 実行ポリシーと署名/信頼性の確保が必要 |
| C. .NET 8 Worker Service（サービス常駐） | UIなしの Windows サービスとしてバックグラウンド実行 | 真の常駐、サービス管理に載る | 管理者権限必須、通知は別途仕組みが必要 |
| D. .NET 8 + WinUI 3 トレイアプリ（推奨・中期） | Windows App SDK/WinUI 3 でトレイ常駐 + バックグラウンドタスク | モダンUI、WinRTトースト、MSIXで配布/更新が容易 | 初期コストや署名/パッケージングが増える |

## 推奨構成
- **短期 (B)**: PowerShellモジュール化 + タスクスケジューラ。
  - ファイル例
    - `Squirrel-Notifier.psm1`（機能本体）
    - `Install.ps1` / `Uninstall.ps1` / `Test.ps1`
    - `config.json`（間隔、リポジトリ、ログパス等）
  - 配布: GitHub Release zip、PowerShell Gallery (`Install-Module`)、必要なら Chocolatey。
  - 実行: `pwsh` があれば優先、なければ `powershell.exe`。タスクは OnLogon + 2h 間隔など。
  - 通知: BurntToast（未インストール時は標準通知 fallback）。
  - ログ: ローテーション（例: 1MB 超でリネーム）、テキストのみ。
  - 信頼性: 実行ポリシー `RemoteSigned` 前提。将来的に署名を検討。

- **中期 (D)**: .NET 8 / WinUI 3 トレイ常駐アプリ。
  - 構成: WinUI 3 (Windows App SDK) + バックグラウンド `BackgroundTask`/`ThreadPoolTimer` + トースト通知 (`WindowsCommunityToolkit.Notifications`)。
  - 配布: MSIX (自動更新可) もしくは 自己完結型 publish (single-file self-contained) + Startup Task 登録。
  - UI: トレイアイコンから「今すぐチェック」「ログ表示」「設定」などを提供。
  - 権限: 通常ユーザーで動作、サービスは不要。

## 短期実装ステップ（PowerShellモジュール化）
1) **モジュール化**: プロトタイプの関数群を `Squirrel-Notifier.psm1` に移動し、公開関数を絞る。
2) **設定分離**: `config.json` に間隔/リポジトリ/ログパス。デフォルト生成 + ユーザー上書き。
3) **タスク登録スクリプト**: `Install.ps1` でタスクスケジューラ登録（pwsh 優先、未インストールなら PowerShell）。`Uninstall.ps1` で削除。
4) **通知のフォールバック**: BurntToast が無ければ標準通知。初回インストール時に BurntToast 自動インストール。
5) **ログとテスト**: 簡易ローテーションと `Test.ps1`（GitHub API/WSL/通知/タスクの疎通確認）。
6) **配布**: GitHub Release に zip（`Install.ps1` 1コマンド導線）。PowerShell Gallery への公開準備（モジュールメタデータ追加）。

## 中期実装ステップ（WinUI 3 トレイアプリ）
1) **プロジェクト雛形**: .NET 8 / WinUI 3。バックグラウンドタイマーで 2h 間隔チェック。
2) **通知**: `WindowsCommunityToolkit.Notifications` を使い、トースト + アクションボタン（「ダウンロードページを開く」など）。
3) **起動/常駐**: Startup Task（パッケージ化）またはショートカット配置で自動起動。必要に応じてバックグラウンドタスクを `IBackgroundTask` として登録。
4) **設定**: JSON or `ApplicationData.Current.LocalSettings`。GUI から間隔・リポジトリ変更。
5) **配布/更新**: MSIX 署名。自己更新が不要なら GitHub Release + インストーラー。社内利用なら企業証明書で署名。
6) **監視/診断**: イベントログ (EventSource) or ローテーションログ。簡易ヘルスチェックメニュー。

## セキュリティ・運用の観点
- 実行ポリシー: `RemoteSigned` を前提に案内。署名対応を検討（PowerShell、MSIX どちらでも）。
- 通信: GitHub API への TLS1.2+ / User-Agent 明示。失敗時は指数バックオフ。
- 権限: 原則ユーザー権限で動作。サービス化は最小限で。
- ログ: 個人情報なし。サイズ上限とローテーションを徹底。

## 移行ロードマップ
- Phase 0: 現行 `.ps1` + タスクスケジューラ（維持）。
- Phase 1: モジュール化 + Install/Uninstall/Test スクリプトを追加し、配布導線を整備。
- Phase 2: PowerShell Gallery/Chocolatey など配布チャネル追加。
- Phase 3: WinUI 3 トレイアプリ試作 → MSIX パッケージング → パイロット配布。
- Phase 4: 利用状況を踏まえ、PowerShell版と WinUI 版のどちらかを標準に定着させる。
