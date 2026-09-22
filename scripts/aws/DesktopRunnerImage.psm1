# desktop E2E runner の AMI と Launch Template の中身を組み立てる（#380）。
# AWS への書き込みは New-DesktopRunnerImage.ps1 / Initialize-DesktopRunnerLaunchTemplate.ps1 が行い、
# ここでは入力値の検証と、タグ・Launch Template データの形だけを扱う。Pester で契約を固定する。

Set-StrictMode -Version Latest

# 使い捨て instance の terminate と起動元 AMI の許可は、OIDC ロールのポリシーでこのタグを条件にする。
# 値を変えると既存の AMI・instance がポリシーの対象から外れるため、変更時はポリシーと同時に移行する。
$script:ResourceTagKey = 'squirrel-notifier:desktop-e2e'
$script:ImageTagValue = 'runner-image'
$script:EphemeralInstanceTagValue = 'ephemeral-runner'
$script:EphemeralInstanceName = 'squirrel-notifier-desktop-e2e-ephemeral'

$script:IdPatterns = @{
    InstanceId          = '^i-[0-9a-f]{8,17}$'
    ImageId             = '^ami-[0-9a-f]{8,17}$'
    SubnetId            = '^subnet-[0-9a-f]{8,17}$'
    SecurityGroupId     = '^sg-[0-9a-f]{8,17}$'
    InstanceType        = '^[a-z][a-z0-9-]*\.[a-z0-9]+$'
    InstanceProfileName = '^[A-Za-z0-9+=,.@_-]{1,128}$'
    ImageName           = '^[A-Za-z0-9()\[\] ./''@_-]{3,128}$'
}

function Assert-DesktopRunnerValue
{
    param(
        [string]$Name,
        [string]$Value
    )

    if ($Value -cnotmatch $script:IdPatterns[$Name])
    {
        throw "$Name '$Value' の形式が不正です。期待する形式: $($script:IdPatterns[$Name])"
    }
}

function Get-DesktopRunnerResourceTag
{
    <#
    .SYNOPSIS
      AMI と使い捨て instance に付けるタグのキーと値を返す。
    #>
    return [pscustomobject]@{
        Key                    = $script:ResourceTagKey
        ImageValue             = $script:ImageTagValue
        EphemeralInstanceValue = $script:EphemeralInstanceTagValue
    }
}

function New-DesktopRunnerImageTagSpecification
{
    <#
    .SYNOPSIS
      CreateImage の --tag-specifications に渡す値を返す。
    .DESCRIPTION
      AMI だけでなく snapshot にも同じタグを付ける。RunInstances の認可では AMI の snapshot も
      リソースになり得るため、OIDC ロールのポリシーで両方をタグで絞れるようにする。
    #>
    param(
        [Parameter(Mandatory)]
        [string]$ImageName,

        [Parameter(Mandatory)]
        [string]$SourceInstanceId
    )

    Assert-DesktopRunnerValue -Name 'ImageName' -Value $ImageName
    Assert-DesktopRunnerValue -Name 'InstanceId' -Value $SourceInstanceId

    $tags = @(
        [ordered]@{ Key = 'Name'; Value = $ImageName }
        [ordered]@{ Key = $script:ResourceTagKey; Value = $script:ImageTagValue }
        [ordered]@{ Key = 'squirrel-notifier:source-instance'; Value = $SourceInstanceId }
    )

    return @(
        [ordered]@{ ResourceType = 'image'; Tags = $tags }
        [ordered]@{ ResourceType = 'snapshot'; Tags = $tags }
    )
}

function New-DesktopRunnerLaunchTemplateData
{
    <#
    .SYNOPSIS
      使い捨て instance 用 Launch Template の LaunchTemplateData を返す。
    .DESCRIPTION
      - InstanceInitiatedShutdownBehavior=terminate: instance 内から shutdown した場合も EBS を残さない
      - public IP を付ける: runner と SSM Agent は outbound だけで動くが、NAT の無い subnet では
        public IP が無いと外へ出られない。inbound は Security Group で塞ぐ
      - IMDSv2 を必須にする。hop limit は既存 instance と同じ 2
      - UserData は持たせない。LaunchTemplateData は ec2:Describe* で誰でも読めるため、
        資格情報や JIT config を入れる経路にしない
    #>
    param(
        [Parameter(Mandatory)]
        [string]$ImageId,

        [Parameter(Mandatory)]
        [string]$InstanceType,

        [Parameter(Mandatory)]
        [string]$InstanceProfileName,

        [Parameter(Mandatory)]
        [string]$SubnetId,

        [Parameter(Mandatory)]
        [string]$SecurityGroupId
    )

    Assert-DesktopRunnerValue -Name 'ImageId' -Value $ImageId
    Assert-DesktopRunnerValue -Name 'InstanceType' -Value $InstanceType
    Assert-DesktopRunnerValue -Name 'InstanceProfileName' -Value $InstanceProfileName
    Assert-DesktopRunnerValue -Name 'SubnetId' -Value $SubnetId
    Assert-DesktopRunnerValue -Name 'SecurityGroupId' -Value $SecurityGroupId

    $tags = @(
        [ordered]@{ Key = 'Name'; Value = $script:EphemeralInstanceName }
        [ordered]@{ Key = $script:ResourceTagKey; Value = $script:EphemeralInstanceTagValue }
    )

    return [ordered]@{
        ImageId                           = $ImageId
        InstanceType                      = $InstanceType
        IamInstanceProfile                = [ordered]@{ Name = $InstanceProfileName }
        InstanceInitiatedShutdownBehavior = 'terminate'
        NetworkInterfaces                 = @(
            [ordered]@{
                DeviceIndex              = 0
                SubnetId                 = $SubnetId
                Groups                   = @($SecurityGroupId)
                AssociatePublicIpAddress = $true
                DeleteOnTermination      = $true
            }
        )
        MetadataOptions                   = [ordered]@{
            HttpTokens              = 'required'
            HttpEndpoint            = 'enabled'
            HttpPutResponseHopLimit = 2
        }
        TagSpecifications                 = @(
            foreach ($resourceType in @('instance', 'volume', 'network-interface'))
            {
                [ordered]@{ ResourceType = $resourceType; Tags = $tags }
            }
        )
    }
}

function ConvertTo-CanonicalObject
{
    param($Value)

    if ($null -eq $Value -or $Value -is [string] -or $Value -is [ValueType])
    {
        return $Value
    }

    if ($Value -is [System.Collections.IDictionary])
    {
        $sorted = [ordered]@{}
        foreach ($key in @($Value.Keys | Sort-Object))
        {
            $sorted[$key] = ConvertTo-CanonicalObject -Value $Value[$key]
        }
        return $sorted
    }

    if ($Value -is [System.Collections.IEnumerable])
    {
        return , @(foreach ($item in $Value) { ConvertTo-CanonicalObject -Value $item })
    }

    $sortedObject = [ordered]@{}
    foreach ($name in @($Value.PSObject.Properties.Name | Sort-Object))
    {
        $sortedObject[$name] = ConvertTo-CanonicalObject -Value $Value.$name
    }
    return $sortedObject
}

function Test-DesktopRunnerLaunchTemplateDataEqual
{
    <#
    .SYNOPSIS
      既存の LaunchTemplateData（describe-launch-template-versions の JSON）が期待値と同じかを返す。
    .DESCRIPTION
      プロパティの順序だけを無視して比較する。AWS が値を補った場合も「異なる」と判定し、
      新しい version を作る側へ倒す（余分な version が増えるだけで、古い設定が残ることはない）。
    #>
    param(
        [Parameter(Mandatory)]
        $Current,

        [Parameter(Mandatory)]
        $Desired
    )

    # JSON を往復させ、hashtable と PSCustomObject、int と long の違いをそろえる。
    $currentJson = ConvertTo-CanonicalObject -Value ($Current | ConvertTo-Json -Depth 10 | ConvertFrom-Json) | ConvertTo-Json -Depth 10 -Compress
    $desiredJson = ConvertTo-CanonicalObject -Value ($Desired | ConvertTo-Json -Depth 10 | ConvertFrom-Json) | ConvertTo-Json -Depth 10 -Compress

    return $currentJson -ceq $desiredJson
}

Export-ModuleMember -Function @(
    'Assert-DesktopRunnerValue',
    'Get-DesktopRunnerResourceTag',
    'New-DesktopRunnerImageTagSpecification',
    'New-DesktopRunnerLaunchTemplateData',
    'Test-DesktopRunnerLaunchTemplateDataEqual'
)
