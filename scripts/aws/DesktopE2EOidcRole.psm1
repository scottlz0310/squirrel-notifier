# desktop E2E workflow が GitHub OIDC で引き受けるロールのポリシー文書を組み立てる（#380）。
# AWS への書き込みは Initialize-DesktopE2EOidcRole.ps1 が行い、ここでは入力値の検証と
# ポリシーの形だけを扱う。権限の境界は DesktopE2EOidcRole.Tests.ps1 で固定する。

Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'DesktopRunnerImage.psm1')

# ARN や信頼条件へ埋め込む値の形式。wildcard を通すと、ポリシーが意図より広い範囲を許可する。
$script:ValuePatterns = @{
    AccountId          = '^\d{12}$'
    Region             = '^[a-z]{2}(-[a-z]+)+-\d{1,2}$'
    Repository         = '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$'
    Environment        = '^[A-Za-z0-9_.-]+$'
    LegacyInstanceId   = '^i-[0-9a-f]{8,17}$'
    LaunchTemplateId   = '^lt-[0-9a-f]{8,17}$'
    SubnetId           = '^subnet-[0-9a-f]{8,17}$'
    SecurityGroupId    = '^sg-[0-9a-f]{8,17}$'
    InstanceType       = '^[a-z][a-z0-9-]*\.[a-z0-9]+$'
    RunnerRoleName     = '^[A-Za-z0-9+=,.@_-]{1,64}$'
    # 1 階層（/squirrel-notifier など）はアプリ全体の parameter を含んでしまうため、2 階層以上を要求する。
    JitParameterPrefix = '^(/[A-Za-z0-9_.-]+){2,}/?$'
}

$script:OidcProviderHost = 'token.actions.githubusercontent.com'

function Assert-OidcRoleValue
{
    param(
        [string]$Name,
        [string]$Value
    )

    if ($Value -cnotmatch $script:ValuePatterns[$Name])
    {
        throw "$Name '$Value' はポリシーに埋め込めない形式です。期待する形式: $($script:ValuePatterns[$Name])"
    }
}

function New-DesktopE2EOidcTrustPolicy
{
    <#
    .SYNOPSIS
      OIDC ロールの信頼ポリシーを返す。指定した repository の指定した environment の job だけを信頼する。
    .DESCRIPTION
      sub を environment に固定する。environment の deployment branch policy（main / v*）と
      trusted dispatcher が、未保護 ref の workflow 定義をこのロールへ到達させない前提を、
      AWS 側でも成り立たせるため。repo:<owner>/<repo>:* のような前方一致にすると、
      environment を宣言しない任意の branch の job から引き受けられる。
    #>
    param(
        [Parameter(Mandatory)]
        [string]$AccountId,

        [Parameter(Mandatory)]
        [string]$Repository,

        [Parameter(Mandatory)]
        [string]$Environment
    )

    Assert-OidcRoleValue -Name 'AccountId' -Value $AccountId
    Assert-OidcRoleValue -Name 'Repository' -Value $Repository
    Assert-OidcRoleValue -Name 'Environment' -Value $Environment

    return [ordered]@{
        Version   = '2012-10-17'
        Statement = @(
            [ordered]@{
                Effect    = 'Allow'
                Principal = [ordered]@{ Federated = "arn:aws:iam::${AccountId}:oidc-provider/$script:OidcProviderHost" }
                Action    = 'sts:AssumeRoleWithWebIdentity'
                Condition = [ordered]@{
                    StringEquals = [ordered]@{
                        "${script:OidcProviderHost}:aud" = 'sts.amazonaws.com'
                        "${script:OidcProviderHost}:sub" = "repo:${Repository}:environment:$Environment"
                    }
                }
            }
        )
    }
}

function New-DesktopE2EOidcPermissionPolicy
{
    <#
    .SYNOPSIS
      OIDC ロールの inline policy を返す。
    .DESCRIPTION
      既存 instance（-LegacyInstanceId）の Start / Stop は、release が使い捨て方式へ移るまでの
      フォールバックとして残す。使い捨て instance については次に限定する。

      - RunInstances: Launch Template（-LaunchTemplateId）経由で、その default version と同じ
        instance type・subnet・Security Group だけ。起動元は自アカウント所有で runner-image タグの
        付いた AMI だけ
      - CreateTags: RunInstances と同時のタグ付けだけ。既存 instance にタグを付けて terminate の
        条件を満たすことはできない
      - TerminateInstances: ephemeral-runner タグの付いた instance だけ。既存 instance には
        このタグが無いため terminate できない
      - PassRole: runner の instance role を EC2 へ渡す場合だけ
      - JIT config の parameter prefix への Put / Delete

      snapshot の暗号化は AWS 管理キー（aws/ebs）で、KMS の権限は要らない。カスタマー管理キーへ
      切り替える場合は、RunInstances のために grant 系の権限を追加する必要がある。
    #>
    param(
        [Parameter(Mandatory)]
        [string]$AccountId,

        [Parameter(Mandatory)]
        [string]$Region,

        [Parameter(Mandatory)]
        [string]$LegacyInstanceId,

        [Parameter(Mandatory)]
        [string]$LaunchTemplateId,

        [Parameter(Mandatory)]
        [string]$InstanceType,

        [Parameter(Mandatory)]
        [string]$SubnetId,

        [Parameter(Mandatory)]
        [string]$SecurityGroupId,

        [Parameter(Mandatory)]
        [string]$RunnerRoleName,

        [Parameter(Mandatory)]
        [string]$JitParameterPrefix
    )

    Assert-OidcRoleValue -Name 'AccountId' -Value $AccountId
    Assert-OidcRoleValue -Name 'Region' -Value $Region
    Assert-OidcRoleValue -Name 'LegacyInstanceId' -Value $LegacyInstanceId
    Assert-OidcRoleValue -Name 'LaunchTemplateId' -Value $LaunchTemplateId
    Assert-OidcRoleValue -Name 'InstanceType' -Value $InstanceType
    Assert-OidcRoleValue -Name 'SubnetId' -Value $SubnetId
    Assert-OidcRoleValue -Name 'SecurityGroupId' -Value $SecurityGroupId
    Assert-OidcRoleValue -Name 'RunnerRoleName' -Value $RunnerRoleName
    Assert-OidcRoleValue -Name 'JitParameterPrefix' -Value $JitParameterPrefix

    $tag = Get-DesktopRunnerResourceTag
    $ec2 = "arn:aws:ec2:${Region}:${AccountId}"
    $launchTemplateArn = "${ec2}:launch-template/$LaunchTemplateId"
    $jitParameterArn = "arn:aws:ssm:${Region}:${AccountId}:parameter$($JitParameterPrefix.TrimEnd('/'))/*"

    return [ordered]@{
        Version   = '2012-10-17'
        Statement = @(
            [ordered]@{
                Sid      = 'DescribeDesktopE2ERunner'
                Effect   = 'Allow'
                Action   = @('ec2:DescribeInstances', 'ec2:DescribeInstanceStatus')
                Resource = '*'
            }
            [ordered]@{
                Sid      = 'ManageDesktopE2ERunner'
                Effect   = 'Allow'
                Action   = @('ec2:StartInstances', 'ec2:StopInstances')
                Resource = "${ec2}:instance/$LegacyInstanceId"
            }
            [ordered]@{
                Sid       = 'RunEphemeralRunnerInstance'
                Effect    = 'Allow'
                Action    = 'ec2:RunInstances'
                Resource  = "${ec2}:instance/*"
                Condition = [ordered]@{
                    ArnEquals    = [ordered]@{ 'ec2:LaunchTemplate' = $launchTemplateArn }
                    StringEquals = [ordered]@{ 'ec2:InstanceType' = $InstanceType }
                }
            }
            [ordered]@{
                Sid       = 'RunEphemeralRunnerResources'
                Effect    = 'Allow'
                Action    = 'ec2:RunInstances'
                Resource  = @(
                    $launchTemplateArn,
                    "${ec2}:subnet/$SubnetId",
                    "${ec2}:security-group/$SecurityGroupId",
                    "${ec2}:network-interface/*",
                    "${ec2}:volume/*"
                )
                Condition = [ordered]@{
                    ArnEquals = [ordered]@{ 'ec2:LaunchTemplate' = $launchTemplateArn }
                }
            }
            [ordered]@{
                Sid       = 'RunFromRunnerImage'
                Effect    = 'Allow'
                Action    = 'ec2:RunInstances'
                Resource  = "arn:aws:ec2:${Region}::image/*"
                Condition = [ordered]@{
                    StringEquals = [ordered]@{
                        'ec2:Owner'                    = $AccountId
                        "aws:ResourceTag/$($tag.Key)" = $tag.ImageValue
                    }
                }
            }
            [ordered]@{
                Sid       = 'RunFromRunnerImageSnapshot'
                Effect    = 'Allow'
                Action    = 'ec2:RunInstances'
                Resource  = "arn:aws:ec2:${Region}::snapshot/*"
                Condition = [ordered]@{
                    StringEquals = [ordered]@{ "aws:ResourceTag/$($tag.Key)" = $tag.ImageValue }
                }
            }
            [ordered]@{
                Sid       = 'TagEphemeralRunnerOnLaunch'
                Effect    = 'Allow'
                Action    = 'ec2:CreateTags'
                Resource  = @("${ec2}:instance/*", "${ec2}:volume/*", "${ec2}:network-interface/*")
                Condition = [ordered]@{
                    StringEquals = [ordered]@{ 'ec2:CreateAction' = 'RunInstances' }
                }
            }
            [ordered]@{
                Sid       = 'TerminateEphemeralRunner'
                Effect    = 'Allow'
                Action    = 'ec2:TerminateInstances'
                Resource  = "${ec2}:instance/*"
                Condition = [ordered]@{
                    StringEquals = [ordered]@{ "aws:ResourceTag/$($tag.Key)" = $tag.EphemeralInstanceValue }
                }
            }
            [ordered]@{
                Sid       = 'PassRunnerInstanceRole'
                Effect    = 'Allow'
                Action    = 'iam:PassRole'
                Resource  = "arn:aws:iam::${AccountId}:role/$RunnerRoleName"
                Condition = [ordered]@{
                    StringEquals = [ordered]@{ 'iam:PassedToService' = 'ec2.amazonaws.com' }
                }
            }
            [ordered]@{
                Sid      = 'WriteJitRunnerConfig'
                Effect   = 'Allow'
                Action   = @('ssm:PutParameter', 'ssm:DeleteParameter')
                Resource = $jitParameterArn
            }
            [ordered]@{
                Sid       = 'EncryptViaSsmOnly'
                Effect    = 'Allow'
                Action    = 'kms:Encrypt'
                Resource  = '*'
                Condition = [ordered]@{
                    StringEquals = [ordered]@{ 'kms:ViaService' = "ssm.$Region.amazonaws.com" }
                }
            }
        )
    }
}

Export-ModuleMember -Function @(
    'New-DesktopE2EOidcTrustPolicy',
    'New-DesktopE2EOidcPermissionPolicy'
)
