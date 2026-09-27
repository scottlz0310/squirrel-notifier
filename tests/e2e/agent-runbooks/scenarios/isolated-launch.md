---
id: isolated-launch
required: true
---

# isolated-launch: 隔離したデータディレクトリでの起動

テスト用インスタンスが、常駐中の既定インスタンスと並んで起動し、隔離したデータディレクトリだけを使うことを確かめる。

## 前提

- 作業ツリーがクリーンで、HEAD がテスト対象のコミット

## 手順

1. 環境を起動し、出力の JSON を控える（以降 `$run`）

   ```powershell
   $run = pwsh -File tests/e2e/agent-runbooks/tools/Start-RunbookEnvironment.ps1 -Scenario isolated-launch | ConvertFrom-Json
   $uia = 'tests/e2e/agent-runbooks/tools/Invoke-RunbookUia.ps1'
   $ev = Join-Path $run.evidenceDirectory 'uia.jsonl'
   ```

2. 購読が始まるまで待つ

   ```powershell
   pwsh -File $uia -ProcessId $run.appProcessId -EvidencePath $ev -Action WaitDisabled -AutomationId StartButton -TimeoutSeconds 60
   pwsh -File $uia -ProcessId $run.appProcessId -EvidencePath $ev -Action WaitText -AutomationId StatusText -Text '^Subscribed' -TimeoutSeconds 60
   ```

3. プロセスを確かめる。`Get-Process SquirrelNotifier.WinUI3` に `$run.appProcessId` があり、その `Path` が `$run.appPath` であること。常駐中の既定インスタンスがあれば、それも別 PID で動き続けていること
4. 隔離したデータディレクトリにファイルがあることを確かめる: `$run.dataRoot` の `settings.json` と `logs/winui3.log`
5. 後始末と漏れの検査

   ```powershell
   pwsh -File tests/e2e/agent-runbooks/tools/Stop-RunbookEnvironment.ps1 -RunDirectory $run.runDirectory
   ```

## 合否

すべて満たせば pass。

| 基準 | 証跡 |
|---|---|
| 手順 2 の `StartButton` が `isEnabled: false`、`StatusText` が `Subscribed.` | `evidence/uia.jsonl`（kind: `uia`） |
| 手順 3 で、テスト用インスタンスが `$run.appPath` から起動している。既定インスタンスがあれば別 PID で動いている | プロセス一覧（kind: `log`、summary に PID と Path） |
| 手順 4 のファイルがある | ファイル一覧（kind: `log`） |
| `isolation.json` が `isolated: true`、`realSettingsUnchanged: true` | `evidence/isolation.json`（kind: `isolation`） |
