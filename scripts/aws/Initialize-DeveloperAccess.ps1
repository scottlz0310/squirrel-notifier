<#
.SYNOPSIS
  root 資格情報の常用をやめるため、開発者用の IAM ユーザーと Admin ロールを作成する（#405）。
.DESCRIPTION
  root には IAM permission boundary も SCP も適用できず、権限の上限を固定する手段が無い。
  本スクリプトは次を冪等に作成し、日常の作業を最小権限の IAM ユーザーへ移す。

  - Admin ロール（-AdminRoleName）。AdministratorAccess を付け、下記の IAM ユーザーだけを MFA 付きで信頼する
  - IAM ユーザー（-UserName）。アクセスキーもコンソールパスワードも作らない
    - inline policy SquirrelNotifierDesktopE2EOperator（desktop E2E runner の操作、EC2 と本プロジェクトの
      IAM ロールの読み取り、Admin ロールへの AssumeRole）
    - SignInLocalDevelopmentAccess（IAM ユーザーで aws login するのに必要）

  default プロファイル（.mcp.json の AWS MCP Server、エージェント、scripts/aws/*.ps1）は、
  この IAM ユーザーで aws login したものを使う。IAM の変更などこの範囲を外れる作業だけ Admin ロールへ切り替える。
  ポリシーの中身と入力値の検証は DeveloperAccess.psm1 にあり、境界は DeveloperAccess.Tests.ps1 で固定している。

  書き込みの前にすべてのポリシー文書を組み立て、入力値を検証する。ユーザーへの権限付与は最後に行い、
  Admin ロールの作成・更新が失敗したときに、AssumeRole や SSM の権限を持つユーザーが残らないようにする。

  root（または管理者権限）で実行する。何度実行しても同じ状態に収束する。
.NOTES
  実行後の手作業:
  1. root で IAM コンソールを開き、作成したユーザーにコンソールパスワードを設定する。
     aws iam create-login-profile はパスワードをコマンドライン引数へ載せるため使わない。
  2. 同じ画面でそのユーザーに MFA デバイスを登録する。未登録のままでは Admin ロールを引き受けられない。
     CLI から Admin を引き受けるには、認証アプリ（TOTP）型のデバイスが必要になる。AWS CLI の mfa_serial は
     パスキーやセキュリティキーを扱えない。パスキーはコンソールのサインイン用として併用してよい。
     IAM ユーザー自身は IAM の権限を持たないため、後から MFA を追加するときは、コンソールで Admin ロールへ
     切り替えてから（または root で）登録する。
  3. aws login で IAM ユーザーとしてサインインし直す。default の login_session が置き換わる。
     aws sts get-caller-identity の Arn が user/<UserName> になっていることを確認する。
  4. Admin が必要な作業のために ~/.aws/config へ次を追加する。aws login の資格情報を
     source_profile へ直接渡せるかは文書化されていないため、公式に案内されている
     credential_process を経由する。mfa_serial を指定すると AssumeRole の呼び出しで MFA コードを
     求められ、信頼ポリシーの MFA 条件を aws login 側の挙動に依存せず満たせる。
       [profile signin-process]
       credential_process = aws configure export-credentials --profile default --format process
       [profile admin]
       role_arn = <出力の adminRoleArn>
       source_profile = signin-process
       mfa_serial = <手順 2 で登録した TOTP デバイスの ARN（arn:aws:iam::<account>:mfa/<デバイス名>）>
  5. 以降、root は請求とアカウント設定だけに使う。
.EXAMPLE
  .\Initialize-DeveloperAccess.ps1 -UserName developer -InstanceId i-0123456789abcdef0
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$UserName,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$InstanceId,

    [ValidateNotNullOrEmpty()]
    [string]$Region = 'us-east-1',

    [ValidateNotNullOrEmpty()]
    [string]$AdminRoleName = 'SquirrelNotifierAdmin',

    [ValidateNotNullOrEmpty()]
    [string]$ParameterPrefix = '/squirrel-notifier/desktop-e2e',

    [ValidateNotNullOrEmpty()]
    [string]$RunnerRoleName = 'SquirrelNotifierDesktopE2ERunner',

    [ValidateNotNullOrEmpty()]
    [string]$RunnerInstanceProfileName = 'SquirrelNotifierDesktopE2ERunner',

    [ValidateNotNullOrEmpty()]
    [string]$OidcRoleName = 'GitHubActionsSquirrelNotifierDesktopE2E'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'DeveloperAccess.psm1') -Force

$operatorPolicyName = 'SquirrelNotifierDesktopE2EOperator'

function Invoke-AwsCli
{
    <#
    .SYNOPSIS
      aws CLI を呼び出し、失敗を例外へ変換する。
    .DESCRIPTION
      -AllowFailure は「存在しない」を戻り値で判定したい場合と、IAM の伝播待ちの再試行だけに使う。
      それ以外の失敗を握り潰すと、権限不足を設定済みと誤認する。
    #>
    param(
        [string[]]$Arguments,
        [switch]$AllowFailure
    )

    $output = & aws @Arguments 2>&1
    if ($LASTEXITCODE -ne 0)
    {
        if ($AllowFailure)
        {
            return $null
        }

        throw "aws CLI が失敗しました: aws $($Arguments -join ' ')`n$output"
    }

    return ($output | Out-String).Trim()
}

function Invoke-AwsCliWithPolicyDocument
{
    <#
    .SYNOPSIS
      ポリシー文書を一時ファイル経由で aws CLI へ渡す。
    .DESCRIPTION
      JSON を引数へ直接載せると、Windows のコマンドライン解釈で引用符が崩れる。
      -RetryUntilPropagated は、作成直後の IAM ユーザーを信頼ポリシーの principal に使う場合に指定する。
      IAM は結果整合性のため、伝播するまで MalformedPolicyDocument（Invalid principal）になる。
    #>
    param(
        [string[]]$Arguments,
        [string]$OptionName,
        $Document,
        [switch]$RetryUntilPropagated
    )

    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("developer-access-" + [guid]::NewGuid().ToString('N') + '.json')
    Set-Content -LiteralPath $path -Value ($Document | ConvertTo-Json -Depth 10) -Encoding ascii
    try
    {
        $fullArguments = $Arguments + @($OptionName, "file://$path")
        if (-not $RetryUntilPropagated)
        {
            Invoke-AwsCli -Arguments $fullArguments | Out-Null
            return
        }

        $maxAttempts = 12
        for ($attempt = 1; $attempt -le $maxAttempts; $attempt++)
        {
            if ($null -ne (Invoke-AwsCli -Arguments $fullArguments -AllowFailure))
            {
                return
            }

            Write-Verbose "IAM ユーザーがまだ伝播していません。再試行します ($attempt/$maxAttempts)。"
            Start-Sleep -Seconds 5
        }

        # 最後の 1 回は例外を伝播させ、aws CLI の出力を原因として残す。
        Invoke-AwsCli -Arguments $fullArguments | Out-Null
    }
    finally
    {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
}

$accountId = Invoke-AwsCli -Arguments @('sts', 'get-caller-identity', '--query', 'Account', '--output', 'text')
Write-Verbose "AWS account: $accountId"

# 書き込みより前に組み立てる。入力値の検証もここで行われ、不正な値では何も作らずに止まる。
$operatorPolicy = New-DeveloperOperatorPolicy `
    -AccountId $accountId `
    -Region $Region `
    -InstanceId $InstanceId `
    -ParameterPrefix $ParameterPrefix `
    -AdminRoleName $AdminRoleName `
    -RunnerRoleName $RunnerRoleName `
    -InstanceProfileName $RunnerInstanceProfileName `
    -OidcRoleName $OidcRoleName
$trustPolicy = New-DeveloperAdminTrustPolicy -AccountId $accountId -UserName $UserName

$existingUser = Invoke-AwsCli -Arguments @('iam', 'get-user', '--user-name', $UserName, '--query', 'User.UserName', '--output', 'text') -AllowFailure
$userCreated = $false
if ($null -eq $existingUser)
{
    if ($PSCmdlet.ShouldProcess($UserName, 'IAM ユーザーを作成'))
    {
        Invoke-AwsCli -Arguments @('iam', 'create-user', '--user-name', $UserName) | Out-Null
        $userCreated = $true
    }
}
else
{
    Write-Verbose "IAM ユーザーは作成済みです: $UserName"
}

$existingRole = Invoke-AwsCli -Arguments @('iam', 'get-role', '--role-name', $AdminRoleName, '--query', 'Role.RoleName', '--output', 'text') -AllowFailure
if ($null -eq $existingRole)
{
    if ($PSCmdlet.ShouldProcess($AdminRoleName, 'Admin ロールを作成'))
    {
        Invoke-AwsCliWithPolicyDocument `
            -Arguments @('iam', 'create-role', '--role-name', $AdminRoleName, '--description', 'squirrel-notifier developer admin role assumed from the IAM user with MFA (#405)') `
            -OptionName '--assume-role-policy-document' `
            -Document $trustPolicy `
            -RetryUntilPropagated:$userCreated
    }
}
else
{
    # 既存ロールの信頼ポリシーを上書きし、別のユーザーや MFA 条件なしの信頼が残らないようにする。
    if ($PSCmdlet.ShouldProcess($AdminRoleName, '信頼ポリシーを更新'))
    {
        Invoke-AwsCliWithPolicyDocument `
            -Arguments @('iam', 'update-assume-role-policy', '--role-name', $AdminRoleName) `
            -OptionName '--policy-document' `
            -Document $trustPolicy `
            -RetryUntilPropagated:$userCreated
    }
}

if ($PSCmdlet.ShouldProcess($AdminRoleName, 'AdministratorAccess をアタッチ'))
{
    Invoke-AwsCli -Arguments @(
        'iam', 'attach-role-policy',
        '--role-name', $AdminRoleName,
        '--policy-arn', 'arn:aws:iam::aws:policy/AdministratorAccess'
    ) | Out-Null
}

# ユーザーへの権限付与はここから。ここまでのどこかで失敗した場合、ユーザーは権限を持たないまま残る。
if ($PSCmdlet.ShouldProcess($UserName, "inline policy $operatorPolicyName を適用"))
{
    Invoke-AwsCliWithPolicyDocument `
        -Arguments @('iam', 'put-user-policy', '--user-name', $UserName, '--policy-name', $operatorPolicyName) `
        -OptionName '--policy-document' `
        -Document $operatorPolicy
}

if ($PSCmdlet.ShouldProcess($UserName, 'SignInLocalDevelopmentAccess をアタッチ'))
{
    Invoke-AwsCli -Arguments @(
        'iam', 'attach-user-policy',
        '--user-name', $UserName,
        '--policy-arn', 'arn:aws:iam::aws:policy/SignInLocalDevelopmentAccess'
    ) | Out-Null
}

[pscustomobject]@{
    schemaVersion      = 1
    accountId          = $accountId
    region             = $Region
    userName           = $UserName
    userArn            = "arn:aws:iam::${accountId}:user/$UserName"
    operatorPolicyName = $operatorPolicyName
    instanceId         = $InstanceId
    parameterPrefix    = $ParameterPrefix
    adminRoleName      = $AdminRoleName
    adminRoleArn       = "arn:aws:iam::${accountId}:role/$AdminRoleName"
} | ConvertTo-Json -Depth 4
