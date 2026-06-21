<#
.SYNOPSIS
    テスト用自己署名 CA 証明書を作成し、MSI パッケージへの署名に使用する PFX を生成します。

.DESCRIPTION
    1. 自己署名 CA 証明書（ルート証明書）を作成
    2. CA をローカルマシンの「信頼されたルート証明機関」ストアへ登録
    3. CA で署名したコード署名証明書を作成
    4. コード署名証明書を PFX ファイルとしてエクスポート

    生成した PFX を使って MSI に署名する例:
        signtool.exe sign /fd SHA256 /p <PfxPassword> /f <PfxPath> SquirrelNotifier-Setup-x64.msi

    本スクリプトはローカル開発・テスト目的専用です。
    本番環境への配布には商用コード署名証明書を使用してください。

.PARAMETER OutputDir
    証明書ファイルの出力先ディレクトリ。既定値: スクリプトと同じディレクトリの cert/ フォルダ。

.PARAMETER PfxPassword
    PFX ファイルの保護パスワード。既定値: "SquirrelNotifierTest"

.PARAMETER Force
    既存の証明書を上書きする場合に指定します。

.EXAMPLE
    .\create-cert.ps1
    既定のパスワードでテスト証明書を生成します。

.EXAMPLE
    .\create-cert.ps1 -PfxPassword "MySecret" -OutputDir "C:\certs"
    指定したパスワードと出力先で証明書を生成します。
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$OutputDir = (Join-Path $PSScriptRoot "cert"),

    [Parameter(Mandatory = $false)]
    [string]$PfxPassword = "SquirrelNotifierTest",

    [Parameter(Mandatory = $false)]
    [switch]$Force
)

$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Squirrel Notifier 自己署名証明書生成スクリプト" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# 管理者権限チェック（ルート証明書ストアへの登録に必要）
$currentUser = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($currentUser)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "このスクリプトは管理者権限で実行してください（ルート証明書ストアへの登録に必要です）。"
    exit 1
}

# 出力ディレクトリ作成
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    Write-Host "出力ディレクトリを作成しました: $OutputDir" -ForegroundColor Green
}

$caCertPath   = Join-Path $OutputDir "SquirrelNotifierCA.cer"
$signCertPath = Join-Path $OutputDir "SquirrelNotifierSign.pfx"

# 既存ファイルの確認
if ((Test-Path $caCertPath) -or (Test-Path $signCertPath)) {
    if (-not $Force) {
        Write-Warning "証明書ファイルが既に存在します。上書きするには -Force を指定してください。"
        Write-Host "  $caCertPath"
        Write-Host "  $signCertPath"
        exit 0
    }
    Write-Host "既存の証明書ファイルを削除します..." -ForegroundColor Yellow
}

# Step 1: 自己署名 CA 証明書を作成（Cert:\CurrentUser\My に一時登録）
Write-Host ""
Write-Host "Step 1: 自己署名 CA 証明書を作成中..." -ForegroundColor Cyan

$caParams = @{
    Subject           = "CN=Squirrel Notifier Test CA, O=scottlz0310, C=JP"
    CertStoreLocation = "Cert:\CurrentUser\My"
    KeyUsage          = "CertSign", "CRLSign"
    KeyUsageProperty  = "Sign"
    KeyAlgorithm      = "RSA"
    KeyLength         = 4096
    HashAlgorithm     = "SHA256"
    NotAfter          = (Get-Date).AddYears(10)
    KeyExportPolicy   = "Exportable"
    TextExtension     = @("2.5.29.19={critical}{text}CA=true")
}
$caCert = New-SelfSignedCertificate @caParams
Write-Host "  CA 証明書を作成しました: $($caCert.Thumbprint)" -ForegroundColor Green

# Step 2: CA をローカルマシンの信頼されたルート証明機関に登録
Write-Host ""
Write-Host "Step 2: CA をローカルマシンのルート証明機関ストアへ登録中..." -ForegroundColor Cyan

$caCertExported = Export-Certificate -Cert $caCert -FilePath $caCertPath -Force
Import-Certificate -FilePath $caCertPath -CertStoreLocation "Cert:\LocalMachine\Root" | Out-Null
Write-Host "  CA 証明書を信頼されたルート証明機関に登録しました。" -ForegroundColor Green
Write-Host "  ファイル: $caCertPath" -ForegroundColor Green

# Step 3: CA で署名したコード署名証明書を作成
Write-Host ""
Write-Host "Step 3: CA 署名コード署名証明書を作成中..." -ForegroundColor Cyan

$signParams = @{
    Subject           = "CN=Squirrel Notifier Code Signing, O=scottlz0310, C=JP"
    CertStoreLocation = "Cert:\CurrentUser\My"
    Signer            = $caCert
    Type              = "CodeSigningCert"
    KeyAlgorithm      = "RSA"
    KeyLength         = 2048
    HashAlgorithm     = "SHA256"
    NotAfter          = (Get-Date).AddYears(5)
    KeyExportPolicy   = "Exportable"
}
$signCert = New-SelfSignedCertificate @signParams
Write-Host "  コード署名証明書を作成しました: $($signCert.Thumbprint)" -ForegroundColor Green

# Step 4: コード署名証明書を PFX でエクスポート
Write-Host ""
Write-Host "Step 4: PFX ファイルへエクスポート中..." -ForegroundColor Cyan

$pfxPwd = ConvertTo-SecureString -String $PfxPassword -Force -AsPlainText
Export-PfxCertificate -Cert $signCert -FilePath $signCertPath -Password $pfxPwd -ChainOption BuildChain -Force | Out-Null
Write-Host "  PFX をエクスポートしました: $signCertPath" -ForegroundColor Green

$caThumbprint = $caCert.Thumbprint

# 一時証明書をストアから削除（CA は Root に登録済み、Sign は CurrentUser\My から削除）
Remove-Item -Path "Cert:\CurrentUser\My\$($caCert.Thumbprint)" -DeleteKey -ErrorAction SilentlyContinue
Remove-Item -Path "Cert:\CurrentUser\My\$($signCert.Thumbprint)" -DeleteKey -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "証明書の生成が完了しました！" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "生成ファイル:" -ForegroundColor Cyan
Write-Host "  CA 証明書 (信頼済み): $caCertPath"
Write-Host "  署名用 PFX:           $signCertPath"
Write-Host "  PFX パスワード:       <-PfxPassword で指定した値>"
Write-Host ""
Write-Host "MSI への署名コマンド例:" -ForegroundColor Cyan
Write-Host "  signtool.exe sign /fd SHA256 /f `"$signCertPath`" /p <PfxPassword> /t http://timestamp.digicert.com SquirrelNotifier-Setup-x64.msi"
Write-Host ""
Write-Host "テスト用 CA 証明書のクリーンアップ（検証完了後に実行・管理者権限必須）:" -ForegroundColor Cyan
Write-Host "  Remove-Item -Path `"Cert:\LocalMachine\Root\$caThumbprint`" -DeleteKey"
Write-Host "  CA サムプリント: $caThumbprint"
Write-Host ""
Write-Host "注意: 本証明書はテスト目的専用です。本番配布には商用証明書を使用してください。" -ForegroundColor Yellow
