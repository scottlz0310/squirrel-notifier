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

## 未対応

ログオン時の自動起動、更新の適用と自己再起動、`.appinstaller` の配信、リリース工程への組み込み、MSI からの移行は未対応です（#569）。パッケージ版は、起動するとウィンドウを表示します。
