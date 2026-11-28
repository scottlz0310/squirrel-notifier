# MSIX パッケージ（移行中）

配布方式を MSI から MSIX（自己署名 + 信頼ストア登録 + `.appinstaller`）へ移行しています（[#569](https://github.com/scottlz0310/squirrel-notifier/issues/569)）。**現在の正式な配布物は MSI です。** MSIX はパッケージの生成までで、リリースには含まれません。

## 構成

| 場所 | 内容 |
|---|---|
| `winui3/SquirrelNotifier.Msix/AppxManifest.xml` | マニフェスト。版は生成時に csproj の `<Version>`（4 番目は 0）へ置き換える |
| `winui3/SquirrelNotifier.Msix/Assets/` | ロゴ。`assets/squirrel-notifier.png` から作成した |
| `scripts/Build-MsixPackage.ps1` | 生成と署名 |

パッケージ名は `scottlz0310.SquirrelNotifier`、発行者は `CN=scottlz0310` です。どちらかを変えると、導入済みの環境を更新できなくなります。

## 仮想化の除外

パッケージ化したアプリが `AppData` に新しく作るファイルは、既定ではアプリ専用の場所に書かれ、ほかのプロセスから読めません。`review-status.json` などの[公開契約](review-status-contract.md)を保つため、`%LocalAppData%\SquirrelNotifier` を `ExcludedDirectory` で除外し、必要な制限付き capability `unvirtualizedResources` を宣言します。起動したエージェント CLI（パッケージ外の exe）は仮想化されません（#569 の検証結果）。

## 生成

```powershell
dotnet publish winui3\SquirrelNotifier.WinUI3\SquirrelNotifier.WinUI3.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -o publish\x64
.\scripts\Build-MsixPackage.ps1 -PayloadDirectory publish\x64
```

対象は x64 だけで、実行ファイルが x64 でなければ生成前に失敗します。生成と署名がすべて成功してから出力先を置き換えるため、失敗しても同じ版の既存パッケージは残ります。

`makeappx` と `signtool` は、`Directory.Packages.props` が指定する `Microsoft.Windows.SDK.BuildTools` の NuGet パッケージから解決します。署名するには `-CertificateThumbprint`（`CurrentUser\My` の証明書）と、必要に応じて `-TimestampUrl` を指定します。

## 試験導入

未署名のパッケージは導入できません。手元のマシンの証明書ストアを変えないよう、試験は Windows Sandbox で行います。

```powershell
# Sandbox 内の管理者 PowerShell で実行する
$cert = New-SelfSignedCertificate -Type Custom -Subject 'CN=scottlz0310' -KeyUsage DigitalSignature `
    -CertStoreLocation Cert:\CurrentUser\My -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
Export-Certificate -Cert $cert -FilePath .\msix-test.cer
signtool sign /fd SHA256 /sha1 $cert.Thumbprint .\SquirrelNotifier-<版>-x64.msix
Import-Certificate -FilePath .\msix-test.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Add-AppxPackage .\SquirrelNotifier-<版>-x64.msix
```

署名鍵の保管と、利用するマシンでの信頼登録の手順は、まだ決めていません（#569）。

## 自動起動

パッケージ版は、タスクスケジューラの代わりにマニフェストのスタートアップタスク（`SquirrelNotifierStartup`）を使います。設定画面の「自動起動」で切り替え、有効にするとログオン時にトレイへ常駐します。

- 既定は無効です。一度アプリを起動して、設定画面で有効にします。
- Windows の「設定 > アプリ > スタートアップ」で無効にした場合は、アプリからは有効にできません。同じ画面で有効にします。
- 引数は渡さず、起動の種別（`StartupTask`）でトレイ常駐を判定します。
- MSI 版が登録したタスク `Squirrel Notifier` は、パッケージ版の設定画面では扱いません。残っていると、ログオン時に MSI 版も起動します。削除は MSI からの移行の手順に含めます。

## 更新の適用

`.appinstaller` から導入したパッケージは、アプリ内で更新を適用して再起動します。

1. 更新の確認は、導入に使った `.appinstaller` を取得し、`MainPackage` の版を実行中のパッケージの版と比べます。確認の契機は、現行と同じ（ウィンドウを表示する起動と、トレイメニューの「アプリの更新を確認」）です。
2. 「更新して再起動」を選ぶと、実行中のレビューがなくなり、起動から 65 秒が経過するまで待ちます。Windows は起動から 60 秒未満のプロセスを再起動しないためです。待つ間は新しいレビューを開始せず、「アプリの更新を確認」から取りやめられます。
3. 購読を止め、保留中のレビューを `update-carryover.json` に保存してから、`RegisterApplicationRestart` と `PackageManager.AddPackageByUriAsync`（`ForceTargetAppShutdown`）で適用します。追加の capability は要りません。
4. 再起動後、保存したレビューを受信したときと同じ経路で処理します。ウィンドウを表示していなかった場合は `--tray` で起動します。

適用に失敗した場合と、再起動されずに戻った場合は、トレイに理由を通知し、購読を再開します。`.msix` を直接導入した場合は、MSI 版と同じく GitHub の最新リリースを確認してリリースページを開きます。

### 実験で確認した挙動

Windows Sandbox（OS build 26100）で、`runFullTrust` だけを持つ実験用アプリで確認しました。

| 操作 | 結果 |
|---|---|
| `.appinstaller` を指定した適用（起動から 60 秒以上） | 旧プロセスが閉じ、新版が指定の引数で起動する。`.appinstaller` の関連付けとスタートアップタスクの状態は維持される |
| 起動から 60 秒未満の適用 | 適用されるが、再起動されない |
| `.appinstaller` に `DeferRegistrationWhenPackagesAreInUse` | `0x80070057` で失敗する |
| `.msix` に Defer を指定して `AppInstance.Restart` | 旧版のまま起動し直し、全プロセスの終了後に適用される |

## 未対応

常駐中の定期的な更新の確認と通知、`.appinstaller` の配信、リリース工程への組み込み、MSI からの移行は未対応です（#569）。
