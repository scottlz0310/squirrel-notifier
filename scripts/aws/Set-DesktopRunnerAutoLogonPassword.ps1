<#
.SYNOPSIS
  自動ログオン用パスワードを SSM Parameter Store へ SecureString として登録する（#392）。
.DESCRIPTION
  パスワードは対話入力で受け取り、値を引数として外部プロセスへ渡さない。

  AWS CLI の `put-parameter --value <パスワード>` は、値を aws.exe のコマンドライン引数として
  載せるため、shell の履歴と実行中プロセスのコマンドラインの両方に平文を残す。SecureString
  として保存しても、登録の時点で資格情報が露出する。AWS Tools for PowerShell なら値を .NET の
  呼び出し引数として渡せるため、本スクリプトはそちらだけを使う。

  登録したパスワードは、instance 内の bootstrap（Setup-DesktopRunnerHost.ps1）が取得して
  LSA secret へ格納する。registry へ平文で書かれることはない。
.EXAMPLE
  .\Set-DesktopRunnerAutoLogonPassword.ps1
.EXAMPLE
  .\Set-DesktopRunnerAutoLogonPassword.ps1 -Region us-east-1 -Password (Read-Host -AsSecureString 'パスワード')
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateNotNullOrEmpty()]
    [string]$ParameterName = '/squirrel-notifier/desktop-e2e/autologon-password',

    [ValidateNotNullOrEmpty()]
    [string]$Region = 'us-east-1',

    [securestring]$Password
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$writeCmdlet = Get-Command -Name 'Write-SSMParameter' -ErrorAction SilentlyContinue
if ($null -eq $writeCmdlet)
{
    Import-Module -Name 'AWS.Tools.SimpleSystemsManagement' -ErrorAction SilentlyContinue
    $writeCmdlet = Get-Command -Name 'Write-SSMParameter' -ErrorAction SilentlyContinue
}

if ($null -eq $writeCmdlet)
{
    throw @'
Write-SSMParameter が見つかりません。次を実行してから再試行してください。

    Install-Module -Name AWS.Tools.SimpleSystemsManagement -Scope CurrentUser

AWS CLI の put-parameter は --value をプロセスのコマンドラインへ載せるため、この手順では使いません。
'@
}

if ($null -eq $Password)
{
    $Password = Read-Host -AsSecureString 'desktop E2E runner の自動ログオンに使う Administrator パスワード'
}

if ($Password.Length -eq 0)
{
    throw 'パスワードが空です。'
}

if ($PSCmdlet.ShouldProcess($ParameterName, 'SSM Parameter Store へ SecureString として登録'))
{
    $plain = $null
    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password)
    try
    {
        $plain = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
        Write-SSMParameter -Name $ParameterName -Type 'SecureString' -Value $plain -Overwrite $true -Region $Region | Out-Null
    }
    finally
    {
        [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
        $plain = $null
        [System.GC]::Collect()
    }
}

[pscustomobject]@{
    schemaVersion = 1
    parameterName = $ParameterName
    region        = $Region
    type          = 'SecureString'
} | ConvertTo-Json -Depth 3
