# 人間の開発者が AWS へアクセスするための IAM ポリシー文書を組み立てる（#405）。
# root 資格情報の常用をやめ、default プロファイル（AWS MCP Server・エージェント・scripts/aws が使う）の
# 権限を desktop E2E runner の操作に限定する。その境界をここで固定し、Pester で検証する。

Set-StrictMode -Version Latest

function New-DeveloperOperatorPolicy
{
    <#
    .SYNOPSIS
      default プロファイルの IAM ユーザーへ付ける inline policy を返す。
    .DESCRIPTION
      変更系の操作は runner instance、Run Command のドキュメント、parameter prefix に限定する。
      Describe 系と Run Command の結果取得はリソース単位の制限をサポートしないため "*" とする。

      IAM の書き込み権限は持たせない。このユーザーの資格情報を得る経路を MFA 付きの
      コンソールサインイン（aws login）だけに限る前提（アクセスキーを作らない）を、
      ユーザー自身の権限で崩せないようにするため。
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
        [string]$AdminRoleName
    )

    $instanceArn = "arn:aws:ec2:${Region}:${AccountId}:instance/$InstanceId"
    $documentArn = "arn:aws:ssm:${Region}::document/AWS-RunPowerShellScript"
    $parameterArn = "arn:aws:ssm:${Region}:${AccountId}:parameter$($ParameterPrefix.TrimEnd('/'))/*"
    $adminRoleArn = "arn:aws:iam::${AccountId}:role/$AdminRoleName"

    return [ordered]@{
        Version   = '2012-10-17'
        Statement = @(
            [ordered]@{
                # ssm:DescribeInstanceInformation はスクリプトからは呼ばないが、Run Command が
                # 届かないときに最初に確認する SSM 登録状態の読み取りなので含める。
                Sid      = 'DescribeDesktopE2ERunner'
                Effect   = 'Allow'
                Action   = @(
                    'ec2:DescribeInstances',
                    'ec2:DescribeInstanceStatus',
                    'ec2:DescribeIamInstanceProfileAssociations',
                    'ssm:DescribeInstanceInformation'
                )
                Resource = '*'
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
      Admin ロールの信頼ポリシーを返す。指定した IAM ユーザーだけを信頼する。
    .DESCRIPTION
      MFA 条件は付けない。このユーザーの資格情報を得る経路は MFA 付きのコンソールサインイン
      （aws login）だけで、MFA は認証の時点で必ず通る。aws login が発行する資格情報が
      MFA コンテキストを持つかは文書化されておらず、条件を付けると Admin を使えなくなり、
      結局 root へ戻る運用を招く。
    #>
    param(
        [Parameter(Mandatory)]
        [string]$AccountId,

        [Parameter(Mandatory)]
        [string]$UserName
    )

    return [ordered]@{
        Version   = '2012-10-17'
        Statement = @(
            [ordered]@{
                Effect    = 'Allow'
                Principal = [ordered]@{ AWS = "arn:aws:iam::${AccountId}:user/$UserName" }
                Action    = 'sts:AssumeRole'
            }
        )
    }
}

Export-ModuleMember -Function @(
    'New-DeveloperOperatorPolicy',
    'New-DeveloperAdminTrustPolicy'
)
