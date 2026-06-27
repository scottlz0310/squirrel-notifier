## Squirrel Notifier v0.1.3 — 安定化・初期設定まわりの改善

v0.1.2 で残存していた自動起動まわりの問題（UAC 有効環境での Access denied）を根本修正し、初回利用時のオンボーディング体験を改善したパッチリリースです。

---

## 変更内容

### 追加
- **オンボーディング案内の表示**: 自動起動（タスクスケジューラ）が未登録の場合に、メインウィンドウ上部に設定画面への誘導を促す InfoBar を表示する機能を追加（#83）

### バグ修正
- **mcp-gateway 接続エラーの改善**: エンドポイントが 404 を返した際や接続エラーが発生した際のエラーメッセージをパースし、設定画面への確認を促す分かりやすいメッセージに変換するよう改善。また、ログに `[CONN_REFUSED]`、`[HTTP_404]`、`[AUTH_ERROR]` などのエラータグを出力するようにした（#85）
- **`--tray` モード即時終了の修正**: トレイモードで起動した際、WinUI 3 (Windows App SDK) の仕様で `Window.Activate` を呼ばないとメッセージループが開始せずプロセスが即時終了してしまう問題を修正（#84）
- **自動起動 Access denied の修正**: Settings の「自動起動」トグルで `schtasks.exe /SC ONLOGON` が UAC 有効環境で Access denied になる問題を修正。`Register-ScheduledTask` / `Unregister-ScheduledTask` PowerShell コマンドレット（CIM API 経由）に切り替え、非昇格プロセスでも登録・削除できるようにした（#86）

### 変更
- 「自動起動」トグルのオン・オフ時に確認ダイアログを表示するようにした。「いいえ」を選択するとトグルが元の状態に戻り、操作を中断できる（#86）
- `mcp-resource-subscriber` コマンドの自動検索に `pnpm bin -g` によるグローバル bin ディレクトリのフォールバックを追加。PATH に含まれない環境でも自動解決できるようになった（#88）
- `SettingsService.ResolveCommandPath` に重複していた `McpSubscriptionService` 内の同名メソッドを統合（#88）

---

## インストール

### MSI インストーラー（推奨）
`SquirrelNotifier-Setup-0.1.3-x64.msi` をダウンロードしてダブルクリックで実行してください。

> **注意:** 環境によっては自動起動タスクの登録がスキップされる場合があります（インストール自体は正常に完了します）。タスクを登録する場合はインストール後にセットアップ Zip 内の `install.cmd` を実行するか、アプリの Settings 画面からタスクを登録してください。

### セットアップ Zip
`SquirrelNotifier-Setup-0.1.3-x64.zip` を展開後、`install.cmd`（または `install.ps1`）を実行してください：

```cmd
rem ダブルクリック、またはコマンドプロンプトから実行
install.cmd -StartMinimized
```

```powershell
# PowerShell から直接実行する場合
.\install.ps1 -StartMinimized
```

---

## 前提条件

| 項目 | 内容 |
|------|------|
| OS | Windows 11 24H2 (10.0.26100) 以降 |
| ランタイム | .NET 10（self-contained のためインストール不要） |
| 外部ツール | `mcp-resource-subscriber`（MCP 購読機能を使用する場合） |

> **注意:** `mcp-resource-subscriber` は本アプリには同梱されていません。
> MCP 購読機能を使用する場合は別途インストールし、設定画面の「Subscriber Command Path」に実行ファイルのパスを指定してください。

---

**Full Changelog**: https://github.com/scottlz0310/squirrel-notifier/compare/v0.1.2...v0.1.3
