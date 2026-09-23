<#
.SYNOPSIS
  desktop E2E workflow が GitHub OIDC で引き受けるロールの信頼ポリシーと inline policy を適用する（#380）。
.DESCRIPTION
  ロール（-RoleName）が無ければ作成し、あれば信頼ポリシーを上書きする。続けて inline policy
  （-PolicyName）を上書きする。中身は DesktopE2EOidcRole.psm1 で組み立てる。

  - 信頼ポリシー: sub を repo:<Repository>:environment:<Environment> に固定する
  - inline policy: 既存 instance の Start / Stop に加え、Launch Template からの使い捨て instance の
    起動・terminate、runner role の PassRole、JIT config の parameter への書き込み

  RunInstances の許可に使う instance type・subnet・Security Group は、Launch Template の default version
  から読む。Launch Template を更新したら、本スクリプトも再実行する。

  読み取りはすべて書き込みより前に済ませる。書き込みは信頼ポリシーを先に行う。inline policy の更新が
  失敗しても、広い信頼のまま新しい権限が付いた状態を残さないため。-PolicyName 以外の inline policy や
  managed policy が付いていれば、本スクリプトの管理外として警告し、出力に列挙する（自動では削除しない）。

  Admin ロールで実行する（IAM の変更が必要）。
.EXAMPLE
  $env:AWS_PROFILE = 'admin'
  .\Initialize-DesktopE2EOidcRole.ps1 -LegacyInstanceId i-0123456789abcdef0
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$LegacyInstanceId,

    [ValidateNotNullOrEmpty()]
    [string]$Region = 'us-east-1',

    [ValidateNotNullOrEmpty()]
    [string]$RoleName = 'GitHubActionsSquirrelNotifierDesktopE2E',

    [ValidateNotNullOrEmpty()]
    [string]$PolicyName = 'SquirrelNotifierDesktopE2EInstanceControl',

    [ValidateNotNullOrEmpty()]
    [string]$Repository = 'scottlz0310/squirrel-notifier',

    [ValidateNotNullOrEmpty()]
    [string]$Environment = 'desktop-e2e',

    [ValidateNotNullOrEmpty()]
    [string]$LaunchTemplateName = 'squirrel-notifier-desktop-e2e-runner',

    [ValidateNotNullOrEmpty()]
    [string]$RunnerRoleName = 'SquirrelNotifierDesktopE2ERunner',

    [ValidateNotNullOrEmpty()]
    [string]$JitParameterPrefix = '/squirrel-notifier/desktop-e2e/jit'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'DesktopE2EOidcRole.psm1') -Force

function Invoke-AwsCli
{
    <#
    .SYNOPSIS
      aws CLI を呼び出し、失敗を例外へ変換する。
    .DESCRIPTION
      -AbsentErrorCode は「存在しない」を戻り値で判定したい場合だけに使い、その AWS エラーコードの
      失敗に限って $null を返す。権限不足や通信エラーまで未作成とみなすと、既存のロールを見落とす。
    #>
    param(
        [string[]]$Arguments,
        [string]$AbsentErrorCode
    )

    $output = & aws @Arguments 2>&1
    if ($LASTEXITCODE -ne 0)
    {
        if ($AbsentErrorCode -and ($output | Out-String).Contains("($AbsentErrorCode)"))
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
      ポリシー文書を一時ファイル経由で aws CLI へ渡す。引数へ直接載せると Windows で引用符が崩れる。
    #>
    param(
        [string[]]$Arguments,
        [string]$OptionName,
        $Document
    )

    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("desktop-e2e-oidc-" + [guid]::NewGuid().ToString('N') + '.json')
    Set-Content -LiteralPath $path -Value (ConvertTo-Json -InputObject $Document -Depth 10) -Encoding ascii
    try
    {
        Invoke-AwsCli -Arguments ($Arguments + @($OptionName, "file://$path")) | Out-Null
    }
    finally
    {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
}

$accountId = Invoke-AwsCli -Arguments @('sts', 'get-caller-identity', '--query', 'Account', '--output', 'text')

# 読み取りと組み立てをすべて書き込みより前に済ませる。入力値の検証もここで行われ、
# 不正な値や前提の欠落では何も書き込まずに止まる。
$providerArn = "arn:aws:iam::${accountId}:oidc-provider/token.actions.githubusercontent.com"
$provider = Invoke-AwsCli -Arguments @(
    'iam', 'get-open-id-connect-provider',
    '--open-id-connect-provider-arn', $providerArn,
    '--query', 'Url',
    '--output', 'text'
) -AbsentErrorCode 'NoSuchEntity'
if ($null -eq $provider)
{
    throw "GitHub の OIDC provider（$providerArn）がありません。アカウントに 1 度だけ作成してから再実行してください。"
}

$launchTemplate = Invoke-AwsCli -Arguments @(
    'ec2', 'describe-launch-template-versions',
    '--region', $Region,
    '--launch-template-name', $LaunchTemplateName,
    '--versions', '$Default',
    '--output', 'json'
) -AbsentErrorCode 'InvalidLaunchTemplateName.NotFoundException'
if ($null -eq $launchTemplate)
{
    throw "Launch Template $LaunchTemplateName がありません。先に Initialize-DesktopRunnerLaunchTemplate.ps1 を実行してください。"
}
$version = @(($launchTemplate | ConvertFrom-Json).LaunchTemplateVersions)[0]
$networkInterface = @($version.LaunchTemplateData.NetworkInterfaces)[0]

# volume の上限は Launch Template の AMI の root volume から取る。AMI を作り直して Launch Template の
# version を更新したら、本スクリプトを再実行してポリシーを合わせる。
$image = Invoke-AwsCli -Arguments @(
    'ec2', 'describe-images',
    '--region', $Region,
    '--image-ids', $version.LaunchTemplateData.ImageId,
    '--query', 'Images[0].{RootDeviceName: RootDeviceName, BlockDeviceMappings: BlockDeviceMappings}',
    '--output', 'json'
) | ConvertFrom-Json
$rootVolume = @($image.BlockDeviceMappings | Where-Object { $_.DeviceName -ceq $image.RootDeviceName -and $null -ne $_.PSObject.Properties['Ebs'] }) |
    Select-Object -First 1 | ForEach-Object { $_.Ebs }
if ($null -eq $rootVolume)
{
    throw "Launch Template の AMI $($version.LaunchTemplateData.ImageId) に root device $($image.RootDeviceName) の EBS マッピングがありません。"
}
# gp2 など IOPS・スループットを持たない種類では、上限を 0 にする（キーが無ければ IfExists で素通りする）。
$rootIops = if ($null -ne $rootVolume.PSObject.Properties['Iops']) { [int]$rootVolume.Iops } else { 0 }
$rootThroughput = if ($null -ne $rootVolume.PSObject.Properties['Throughput']) { [int]$rootVolume.Throughput } else { 0 }

$trustPolicy = New-DesktopE2EOidcTrustPolicy -AccountId $accountId -Repository $Repository -Environment $Environment
$permissionPolicy = New-DesktopE2EOidcPermissionPolicy `
    -AccountId $accountId `
    -Region $Region `
    -LegacyInstanceId $LegacyInstanceId `
    -LaunchTemplateId $version.LaunchTemplateId `
    -InstanceType $version.LaunchTemplateData.InstanceType `
    -SubnetId $networkInterface.SubnetId `
    -SecurityGroupId @($networkInterface.Groups)[0] `
    -RootVolumeSize ([int]$rootVolume.VolumeSize) `
    -RootVolumeType $rootVolume.VolumeType `
    -RootVolumeIops $rootIops `
    -RootVolumeThroughput $rootThroughput `
    -RunnerRoleName $RunnerRoleName `
    -JitParameterPrefix $JitParameterPrefix

$existingRole = Invoke-AwsCli -Arguments @('iam', 'get-role', '--role-name', $RoleName, '--query', 'Role.RoleName', '--output', 'text') -AbsentErrorCode 'NoSuchEntity'

# 管理外のポリシーが残っていると、本スクリプトの境界より広い権限が付いたままになる。
$unmanagedInline = @()
$attached = @()
if ($null -ne $existingRole)
{
    $inlinePolicies = @((Invoke-AwsCli -Arguments @('iam', 'list-role-policies', '--role-name', $RoleName, '--output', 'json') | ConvertFrom-Json).PolicyNames)
    $unmanagedInline = @($inlinePolicies | Where-Object { $_ -ne $PolicyName })
    $attached = @((Invoke-AwsCli -Arguments @('iam', 'list-attached-role-policies', '--role-name', $RoleName, '--output', 'json') | ConvertFrom-Json).AttachedPolicies | ForEach-Object { $_.PolicyArn })
    if ($unmanagedInline.Count -gt 0 -or $attached.Count -gt 0)
    {
        Write-Warning "ロール $RoleName に本スクリプトの管理外のポリシーがあります。inline: $($unmanagedInline -join ', ')、managed: $($attached -join ', ')。不要なら Admin で外してください。"
    }
}

if ($null -eq $existingRole)
{
    if ($PSCmdlet.ShouldProcess($RoleName, 'OIDC ロールを作成'))
    {
        Invoke-AwsCliWithPolicyDocument `
            -Arguments @('iam', 'create-role', '--role-name', $RoleName, '--description', 'squirrel-notifier desktop E2E workflow role assumed via GitHub OIDC (#380)') `
            -OptionName '--assume-role-policy-document' `
            -Document $trustPolicy
    }
}
elseif ($PSCmdlet.ShouldProcess($RoleName, '信頼ポリシーを更新'))
{
    Invoke-AwsCliWithPolicyDocument `
        -Arguments @('iam', 'update-assume-role-policy', '--role-name', $RoleName) `
        -OptionName '--policy-document' `
        -Document $trustPolicy
}

if ($PSCmdlet.ShouldProcess($RoleName, "inline policy $PolicyName を適用"))
{
    Invoke-AwsCliWithPolicyDocument `
        -Arguments @('iam', 'put-role-policy', '--role-name', $RoleName, '--policy-name', $PolicyName) `
        -OptionName '--policy-document' `
        -Document $permissionPolicy
}

[pscustomobject]@{
    schemaVersion           = 1
    accountId               = $accountId
    region                  = $Region
    roleName                = $RoleName
    roleArn                 = "arn:aws:iam::${accountId}:role/$RoleName"
    trustedSubject          = "repo:${Repository}:environment:$Environment"
    policyName              = $PolicyName
    launchTemplateId        = $version.LaunchTemplateId
    launchTemplateVersion   = $version.VersionNumber
    instanceType            = $version.LaunchTemplateData.InstanceType
    subnetId                = $networkInterface.SubnetId
    securityGroupId         = @($networkInterface.Groups)[0]
    rootVolume              = [ordered]@{
        imageId    = $version.LaunchTemplateData.ImageId
        deviceName = $image.RootDeviceName
        size       = [int]$rootVolume.VolumeSize
        type       = $rootVolume.VolumeType
        iops       = $rootIops
        throughput = $rootThroughput
    }
    unmanagedInlinePolicies = $unmanagedInline
    attachedPolicies        = $attached
} | ConvertTo-Json -Depth 4
