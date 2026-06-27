---

## インストール

### MSI インストーラー（推奨）
`SquirrelNotifier-Setup-{{VERSION}}-x64.msi` をダウンロードしてダブルクリックで実行してください。

> **注意:** 環境によっては自動起動タスクの登録がスキップされる場合があります（インストール自体は正常に完了します）。タスクを登録する場合はインストール後にセットアップ Zip 内の `install.cmd` を実行するか、アプリの Settings 画面からタスクを登録してください。

### セットアップ Zip
`SquirrelNotifier-Setup-{{VERSION}}-x64.zip` を展開後、`install.cmd`（または `install.ps1`）を実行してください：

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
