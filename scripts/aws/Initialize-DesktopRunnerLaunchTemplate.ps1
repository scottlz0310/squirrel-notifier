<#
.SYNOPSIS
  使い捨て desktop E2E runner の Security Group と Launch Template を作成・更新する（#380）。
.DESCRIPTION
  次を冪等に適用する。

  - Security Group（-SecurityGroupName）。inbound を持たない。runner と SSM Agent は outbound だけで
    動き、自動ログオンの導入後は RDP も要らない。既存の SG に inbound が追加されていたら止まる
  - Launch Template（-LaunchTemplateName）。中身は DesktopRunnerImage.psm1 の
    New-DesktopRunnerLaunchTemplateData で組み立てる。default version の中身が期待値と異なる場合だけ
    新しい version を作り、default にする

  -ImageId には New-DesktopRunnerImage.ps1 で作った AMI を渡す。Get-DesktopRunnerResourceTag の
  タグが無い AMI は OIDC ロールが起動を許可しないため、ここで拒否する。

  Admin ロールで実行する（Security Group と Launch Template の作成・変更が必要）。
.EXAMPLE
  $env:AWS_PROFILE = 'admin'
  .\Initialize-DesktopRunnerLaunchTemplate.ps1 -ImageId ami-0123456789abcdef0 -SubnetId subnet-0123456789abcdef0
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ImageId,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SubnetId,

    [ValidateNotNullOrEmpty()]
    [string]$Region = 'us-east-1',

    [ValidateNotNullOrEmpty()]
    [string]$InstanceType = 'm7i.xlarge',

    [ValidateNotNullOrEmpty()]
    [string]$InstanceProfileName = 'SquirrelNotifierDesktopE2ERunner',

    [ValidateNotNullOrEmpty()]
    [string]$LaunchTemplateName = 'squirrel-notifier-desktop-e2e-runner',

    [ValidateNotNullOrEmpty()]
    [string]$SecurityGroupName = 'squirrel-notifier-desktop-e2e-runner'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'DesktopRunnerImage.psm1') -Force

function Invoke-AwsCli
{
    <#
    .SYNOPSIS
      aws CLI を呼び出し、失敗を例外へ変換する。
    .DESCRIPTION
      -AllowFailure は「存在しない」を戻り値で判定したい場合だけに使う。
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

function Invoke-AwsCliWithJsonFile
{
    <#
    .SYNOPSIS
      JSON を一時ファイル経由で aws CLI へ渡す。引数へ直接載せると Windows で引用符が崩れる。
    #>
    param(
        [string[]]$Arguments,
        [string]$OptionName,
        $Document
    )

    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("desktop-runner-lt-" + [guid]::NewGuid().ToString('N') + '.json')
    Set-Content -LiteralPath $path -Value (ConvertTo-Json -InputObject $Document -Depth 10) -Encoding ascii
    try
    {
        return Invoke-AwsCli -Arguments ($Arguments + @($OptionName, "file://$path"))
    }
    finally
    {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
}

# 書き込みより前に入力値を検証する。Security Group ID は作成後に決まるため、
# Launch Template データの組み立て自体は SG の確定後に行う。
Assert-DesktopRunnerValue -Name 'ImageId' -Value $ImageId
Assert-DesktopRunnerValue -Name 'SubnetId' -Value $SubnetId
Assert-DesktopRunnerValue -Name 'InstanceType' -Value $InstanceType
Assert-DesktopRunnerValue -Name 'InstanceProfileName' -Value $InstanceProfileName

$resourceTag = Get-DesktopRunnerResourceTag

$image = Invoke-AwsCli -Arguments @(
    'ec2', 'describe-images',
    '--region', $Region,
    '--owners', 'self',
    '--image-ids', $ImageId,
    '--output', 'json'
) | ConvertFrom-Json
$imageRecord = @($image.Images) | Select-Object -First 1
if ($null -eq $imageRecord)
{
    throw "AMI $ImageId が自アカウントの所有として見つかりません。"
}
if ($imageRecord.State -ne 'available')
{
    throw "AMI $ImageId は '$($imageRecord.State)' です。available になってから実行してください。"
}
$imageTags = if ($imageRecord.PSObject.Properties.Name -contains 'Tags') { @($imageRecord.Tags) } else { @() }
$imageTagValue = $imageTags | Where-Object { $_.Key -eq $resourceTag.Key } | Select-Object -First 1 -ExpandProperty Value
if ($imageTagValue -ne $resourceTag.ImageValue)
{
    throw "AMI $ImageId にタグ $($resourceTag.Key)=$($resourceTag.ImageValue) がありません。New-DesktopRunnerImage.ps1 で作った AMI を指定してください。"
}

$vpcId = Invoke-AwsCli -Arguments @(
    'ec2', 'describe-subnets',
    '--region', $Region,
    '--subnet-ids', $SubnetId,
    '--query', 'Subnets[0].VpcId',
    '--output', 'text'
)

$securityGroups = Invoke-AwsCli -Arguments @(
    'ec2', 'describe-security-groups',
    '--region', $Region,
    '--filters', "Name=vpc-id,Values=$vpcId", "Name=group-name,Values=$SecurityGroupName",
    '--output', 'json'
) | ConvertFrom-Json
$securityGroup = @($securityGroups.SecurityGroups) | Select-Object -First 1

if ($null -eq $securityGroup)
{
    if (-not $PSCmdlet.ShouldProcess($SecurityGroupName, "Security Group を $vpcId に作成"))
    {
        return
    }

    $securityGroupId = Invoke-AwsCli -Arguments @(
        'ec2', 'create-security-group',
        '--region', $Region,
        '--vpc-id', $vpcId,
        '--group-name', $SecurityGroupName,
        '--description', 'squirrel-notifier desktop E2E ephemeral runner (#380). No inbound rules.',
        '--query', 'GroupId',
        '--output', 'text'
    )
}
else
{
    $securityGroupId = $securityGroup.GroupId
    if (@($securityGroup.IpPermissions).Count -gt 0)
    {
        throw "Security Group $securityGroupId（$SecurityGroupName）に inbound ルールがあります。使い捨て runner は inbound を必要としないため、ルールを削除してから再実行してください。"
    }
}

$desiredData = New-DesktopRunnerLaunchTemplateData `
    -ImageId $ImageId `
    -InstanceType $InstanceType `
    -InstanceProfileName $InstanceProfileName `
    -SubnetId $SubnetId `
    -SecurityGroupId $securityGroupId
$versionDescription = "image $ImageId"

$launchTemplateId = Invoke-AwsCli -Arguments @(
    'ec2', 'describe-launch-templates',
    '--region', $Region,
    '--launch-template-names', $LaunchTemplateName,
    '--query', 'LaunchTemplates[0].LaunchTemplateId',
    '--output', 'text'
) -AllowFailure

if ($null -eq $launchTemplateId)
{
    if (-not $PSCmdlet.ShouldProcess($LaunchTemplateName, 'Launch Template を作成'))
    {
        return
    }

    $launchTemplateId = Invoke-AwsCliWithJsonFile `
        -Arguments @(
            'ec2', 'create-launch-template',
            '--region', $Region,
            '--launch-template-name', $LaunchTemplateName,
            '--version-description', $versionDescription,
            '--query', 'LaunchTemplate.LaunchTemplateId',
            '--output', 'text'
        ) `
        -OptionName '--launch-template-data' `
        -Document $desiredData
    $defaultVersion = 1
}
else
{
    $current = Invoke-AwsCli -Arguments @(
        'ec2', 'describe-launch-template-versions',
        '--region', $Region,
        '--launch-template-id', $launchTemplateId,
        '--versions', '$Default',
        '--output', 'json'
    ) | ConvertFrom-Json
    $currentVersion = @($current.LaunchTemplateVersions)[0]
    $defaultVersion = $currentVersion.VersionNumber

    if (Test-DesktopRunnerLaunchTemplateDataEqual -Current $currentVersion.LaunchTemplateData -Desired $desiredData)
    {
        Write-Verbose "Launch Template $LaunchTemplateName の default version $defaultVersion は期待値と一致しています。"
    }
    elseif ($PSCmdlet.ShouldProcess($LaunchTemplateName, "新しい version（$versionDescription）を作成して default にする"))
    {
        $defaultVersion = Invoke-AwsCliWithJsonFile `
            -Arguments @(
                'ec2', 'create-launch-template-version',
                '--region', $Region,
                '--launch-template-id', $launchTemplateId,
                '--version-description', $versionDescription,
                '--query', 'LaunchTemplateVersion.VersionNumber',
                '--output', 'text'
            ) `
            -OptionName '--launch-template-data' `
            -Document $desiredData

        Invoke-AwsCli -Arguments @(
            'ec2', 'modify-launch-template',
            '--region', $Region,
            '--launch-template-id', $launchTemplateId,
            '--default-version', $defaultVersion
        ) | Out-Null
    }
}

[pscustomobject]@{
    schemaVersion      = 1
    region             = $Region
    launchTemplateId   = $launchTemplateId
    launchTemplateName = $LaunchTemplateName
    defaultVersion     = [int]$defaultVersion
    imageId            = $ImageId
    instanceType       = $InstanceType
    subnetId           = $SubnetId
    vpcId              = $vpcId
    securityGroupId    = $securityGroupId
} | ConvertTo-Json -Depth 4
