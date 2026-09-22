# 人間の開発者が AWS へアクセスするための IAM ポリシー文書を組み立てる（#405）。
# root 資格情報の常用をやめ、default プロファイル（AWS MCP Server・エージェント・scripts/aws が使う）の
# 権限を desktop E2E runner の操作に限定する。その境界をここで固定し、Pester で検証する。

Set-StrictMode -Version Latest

# ARN へ埋め込む値の形式。wildcard や広すぎる値を通すと、ポリシーが意図より広い範囲を許可する
# （例: ParameterPrefix '/' は parameter/*、AdminRoleName '*' は role/* になる）。
$script:ArnValuePatterns = @{
    AccountId       = '^\d{12}$'
    Region          = '^[a-z]{2}(-[a-z]+)+-\d{1,2}$'
    InstanceId      = '^i-[0-9a-f]{8,17}$'
    # 1 階層（/squirrel-notifier など）はアプリ全体の parameter を含んでしまうため、2 階層以上を要求する。
    ParameterPrefix     = '^(/[A-Za-z0-9_.-]+){2,}/?$'
    AdminRoleName       = '^[A-Za-z0-9+=,.@_-]{1,64}$'
    RunnerRoleName      = '^[A-Za-z0-9+=,.@_-]{1,64}$'
    OidcRoleName        = '^[A-Za-z0-9+=,.@_-]{1,64}$'
    InstanceProfileName = '^[A-Za-z0-9+=,.@_-]{1,128}$'
    UserName            = '^[A-Za-z0-9+=,.@_-]{1,64}$'
}

function Assert-ArnValue
{
    param(
        [string]$Name,
        [string]$Value
    )

    if ($Value -cnotmatch $script:ArnValuePatterns[$Name])
    {
        throw "$Name '$Value' は ARN に埋め込めない形式です。期待する形式: $($script:ArnValuePatterns[$Name])"
    }
}

function New-DeveloperOperatorPolicy
{
    <#
    .SYNOPSIS
      default プロファイルの IAM ユーザーへ付ける inline policy を返す。
    .DESCRIPTION
      変更系の操作は runner instance、Run Command のドキュメント、parameter prefix に限定する。
      Describe 系と Run Command の結果取得はリソース単位の制限をサポートしないため "*" とする。

      EC2 の読み取りは ec2:Describe* とする。Admin が作る AMI・Launch Template・Security Group を
      この権限で検証するため（#380）、個別に列挙すると検証のたびに Admin での再適用が要る。

      IAM の書き込み権限は持たせない。アクセスキーを作らず、資格情報を aws login の
      一時資格情報に限る前提を、ユーザー自身の権限で崩せないようにするため。
      IAM の読み取りは、Admin が適用したロールのポリシーを検証できるよう、本プロジェクトの
      ロールと instance profile に限って許可する。
    #>
    param(
        [Parameter(Mandatory)]
        [string]$AccountId,

        [Parameter(Mandatory)]
        [string]$Region,

        [Parameter(Mandatory)]
        [string]$InstanceId,

        [Parameter(Mandatory)]
        [string]$ParameterPrefix,

        [Parameter(Mandatory)]
        [string]$AdminRoleName,

        [Parameter(Mandatory)]
        [string]$RunnerRoleName,

        [Parameter(Mandatory)]
        [string]$InstanceProfileName,

        [Parameter(Mandatory)]
        [string]$OidcRoleName
    )

    Assert-ArnValue -Name 'AccountId' -Value $AccountId
    Assert-ArnValue -Name 'Region' -Value $Region
    Assert-ArnValue -Name 'InstanceId' -Value $InstanceId
    Assert-ArnValue -Name 'ParameterPrefix' -Value $ParameterPrefix
    Assert-ArnValue -Name 'AdminRoleName' -Value $AdminRoleName
    Assert-ArnValue -Name 'RunnerRoleName' -Value $RunnerRoleName
    Assert-ArnValue -Name 'InstanceProfileName' -Value $InstanceProfileName
    Assert-ArnValue -Name 'OidcRoleName' -Value $OidcRoleName

    $instanceArn = "arn:aws:ec2:${Region}:${AccountId}:instance/$InstanceId"
    $documentArn = "arn:aws:ssm:${Region}::document/AWS-RunPowerShellScript"
    $parameterArn = "arn:aws:ssm:${Region}:${AccountId}:parameter$($ParameterPrefix.TrimEnd('/'))/*"
    $adminRoleArn = "arn:aws:iam::${AccountId}:role/$AdminRoleName"
    $projectIamArns = @(
        $adminRoleArn,
        "arn:aws:iam::${AccountId}:role/$RunnerRoleName",
        "arn:aws:iam::${AccountId}:role/$OidcRoleName",
        "arn:aws:iam::${AccountId}:instance-profile/$InstanceProfileName",
        "arn:aws:iam::${AccountId}:oidc-provider/token.actions.githubusercontent.com"
    )

    return [ordered]@{
        Version   = '2012-10-17'
        Statement = @(
            [ordered]@{
                # ssm:DescribeInstanceInformation はスクリプトからは呼ばないが、Run Command が
                # 届かないときに最初に確認する SSM 登録状態の読み取りなので含める。
                # ssm:DescribeParameters は値を返さず、名前と型などのメタデータだけを返す。
                Sid      = 'DescribeDesktopE2EResources'
                Effect   = 'Allow'
                Action   = @(
                    'ec2:Describe*',
                    'ssm:DescribeInstanceInformation',
                    'ssm:DescribeParameters'
                )
                Resource = '*'
            }
            [ordered]@{
                Sid      = 'ReadDesktopE2EIamRoles'
                Effect   = 'Allow'
                Action   = @(
                    'iam:GetRole',
                    'iam:GetRolePolicy',
                    'iam:ListRolePolicies',
                    'iam:ListAttachedRolePolicies',
                    'iam:SimulatePrincipalPolicy',
                    'iam:GetInstanceProfile',
                    'iam:GetOpenIDConnectProvider'
                )
                Resource = $projectIamArns
            }
            [ordered]@{
                Sid      = 'StartStopDesktopE2ERunner'
                Effect   = 'Allow'
                Action   = @('ec2:StartInstances', 'ec2:StopInstances')
                Resource = $instanceArn
            }
            [ordered]@{
                Sid      = 'RunCommandOnDesktopE2ERunner'
                Effect   = 'Allow'
                Action   = 'ssm:SendCommand'
                Resource = @($instanceArn, $documentArn)
            }
            [ordered]@{
                Sid      = 'ReadRunCommandResults'
                Effect   = 'Allow'
                Action   = @('ssm:GetCommandInvocation', 'ssm:ListCommandInvocations', 'ssm:ListCommands')
                Resource = '*'
            }
            [ordered]@{
                Sid      = 'ReadWriteDesktopE2EParameters'
                Effect   = 'Allow'
                Action   = @('ssm:GetParameter', 'ssm:PutParameter')
                Resource = $parameterArn
            }
            [ordered]@{
                Sid       = 'EncryptDecryptViaSsmOnly'
                Effect    = 'Allow'
                Action    = @('kms:Decrypt', 'kms:Encrypt')
                Resource  = '*'
                Condition = [ordered]@{
                    StringEquals = [ordered]@{ 'kms:ViaService' = "ssm.$Region.amazonaws.com" }
                }
            }
            [ordered]@{
                Sid      = 'AssumeAdminRole'
                Effect   = 'Allow'
                Action   = 'sts:AssumeRole'
                Resource = $adminRoleArn
            }
        )
    }
}

function New-DeveloperAdminTrustPolicy
{
    <#
    .SYNOPSIS
      Admin ロールの信頼ポリシーを返す。指定した IAM ユーザーだけを、MFA 付きで信頼する。
    .DESCRIPTION
      MFA を AssumeRole の条件にする。IAM ユーザーに MFA を登録したかをスクリプトは強制できないため、
      未登録のまま AdministratorAccess を引き受けられる状態を信頼ポリシー側で塞ぐ。

      aws login の一時資格情報が MFA コンテキストを持つかは文書化されていない。そのため Admin
      プロファイルには mfa_serial を指定し、AssumeRole の呼び出し自体で MFA コードを渡す。
      これなら aws login 側の挙動に依存せず、MFA 未登録なら引き受けられない（fail-closed）。
    #>
    param(
        [Parameter(Mandatory)]
        [string]$AccountId,

        [Parameter(Mandatory)]
        [string]$UserName
    )

    Assert-ArnValue -Name 'AccountId' -Value $AccountId
    Assert-ArnValue -Name 'UserName' -Value $UserName

    return [ordered]@{
        Version   = '2012-10-17'
        Statement = @(
            [ordered]@{
                Effect    = 'Allow'
                Principal = [ordered]@{ AWS = "arn:aws:iam::${AccountId}:user/$UserName" }
                Action    = 'sts:AssumeRole'
                Condition = [ordered]@{
                    Bool = [ordered]@{ 'aws:MultiFactorAuthPresent' = 'true' }
                }
            }
        )
    }
}

Export-ModuleMember -Function @(
    'New-DeveloperOperatorPolicy',
    'New-DeveloperAdminTrustPolicy'
)
