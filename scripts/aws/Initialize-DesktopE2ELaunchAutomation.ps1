<#
.SYNOPSIS
  desktop E2E の EC2 起動を固定する SSM Automation と専用実行ロールを作成する（#380）。
.DESCRIPTION
  Launch Template の default version を読み取り、その数値を Automation の新しい不変 version に
  埋め込む。OIDC ロールを切り替える前に Admin プロファイルで実行し、出力された documentVersion を
  OIDC ポリシーと workflow の両方に設定する。
.EXAMPLE
  $env:AWS_PROFILE = 'admin'
  .\Initialize-DesktopE2ELaunchAutomation.ps1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Region = 'us-east-1',
    [string]$DocumentName = 'SquirrelNotifierDesktopE2ELaunch',
    [string]$RoleName = 'SquirrelNotifierDesktopE2EAutomation',
    [string]$PolicyName = 'SquirrelNotifierDesktopE2ELaunch',
    [string]$LaunchTemplateName = 'squirrel-notifier-desktop-e2e-runner',
    [string]$RunnerRoleName = 'SquirrelNotifierDesktopE2ERunner',
    [string]$LegacyInstanceId = 'i-00b4e23b910eade6c',
    [string]$JitParameterPrefix = '/squirrel-notifier/desktop-e2e/jit'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'DesktopE2ELaunchAutomation.psm1') -Force

function Invoke-AwsCli
{
    param([string[]]$Arguments, [string]$AbsentErrorCode)

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

function Invoke-AwsCliWithDocument
{
    param([string[]]$Arguments, [string]$OptionName, $Document)

    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("desktop-e2e-automation-" + [guid]::NewGuid().ToString('N') + '.json')
    Set-Content -LiteralPath $path -Value (ConvertTo-Json -InputObject $Document -Depth 20 -Compress -EscapeHandling EscapeNonAscii) -Encoding ascii
    try
    {
        return Invoke-AwsCli -Arguments ($Arguments + @($OptionName, "file://$path"))
    }
    finally
    {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
}

$accountId = Invoke-AwsCli -Arguments @('sts', 'get-caller-identity', '--query', 'Account', '--output', 'text')
$roleArn = "arn:aws:iam::${accountId}:role/$RoleName"

$launchTemplateJson = Invoke-AwsCli -Arguments @(
    'ec2', 'describe-launch-template-versions', '--region', $Region,
    '--launch-template-name', $LaunchTemplateName, '--versions', '$Default', '--output', 'json'
) -AbsentErrorCode 'InvalidLaunchTemplateName.NotFoundException'
if ($null -eq $launchTemplateJson)
{
    throw "Launch Template $LaunchTemplateName がありません。"
}
$template = @(($launchTemplateJson | ConvertFrom-Json).LaunchTemplateVersions)[0]
$networkInterface = @($template.LaunchTemplateData.NetworkInterfaces)[0]

$image = Invoke-AwsCli -Arguments @(
    'ec2', 'describe-images', '--region', $Region, '--image-ids', $template.LaunchTemplateData.ImageId,
    '--query', 'Images[0].{RootDeviceName: RootDeviceName, BlockDeviceMappings: BlockDeviceMappings}', '--output', 'json'
) | ConvertFrom-Json
Assert-DesktopE2ELaunchStorage -LaunchTemplateData $template.LaunchTemplateData -Image $image
$rootVolume = @($image.BlockDeviceMappings | Where-Object { $_.DeviceName -ceq $image.RootDeviceName -and $null -ne $_.PSObject.Properties['Ebs'] }) |
    Select-Object -First 1 | ForEach-Object { $_.Ebs }
if ($null -eq $rootVolume)
{
    throw "AMI $($template.LaunchTemplateData.ImageId) に root EBS マッピングがありません。"
}
$rootIops = if ($null -ne $rootVolume.PSObject.Properties['Iops']) { [int]$rootVolume.Iops } else { 0 }
$rootThroughput = if ($null -ne $rootVolume.PSObject.Properties['Throughput']) { [int]$rootVolume.Throughput } else { 0 }

$runnerPolicyArguments = @{
    AccountId          = $accountId
    Region             = $Region
    LegacyInstanceId   = $LegacyInstanceId
    LaunchTemplateId   = $template.LaunchTemplateId
    InstanceType       = $template.LaunchTemplateData.InstanceType
    SubnetId           = $networkInterface.SubnetId
    SecurityGroupId    = @($networkInterface.Groups)[0]
    RootVolumeSize     = [int]$rootVolume.VolumeSize
    RootVolumeType     = $rootVolume.VolumeType
    RootVolumeIops     = $rootIops
    RootVolumeThroughput = $rootThroughput
    RunnerRoleName     = $RunnerRoleName
    JitParameterPrefix = $JitParameterPrefix
}
$trustPolicy = New-DesktopE2ELaunchAutomationTrustPolicy -AccountId $accountId -Region $Region
$permissionPolicy = New-DesktopE2ELaunchAutomationPermissionPolicy -RunnerPolicyArguments $runnerPolicyArguments
$document = New-DesktopE2ELaunchAutomationDocument -RoleArn $roleArn -LaunchTemplateId $template.LaunchTemplateId -LaunchTemplateVersion $template.VersionNumber

$existingRole = Invoke-AwsCli -Arguments @('iam', 'get-role', '--role-name', $RoleName, '--query', 'Role.RoleName', '--output', 'text') -AbsentErrorCode 'NoSuchEntity'
if ($null -ne $existingRole)
{
    $inline = @((Invoke-AwsCli -Arguments @('iam', 'list-role-policies', '--role-name', $RoleName, '--output', 'json') | ConvertFrom-Json).PolicyNames)
    $attached = @((Invoke-AwsCli -Arguments @('iam', 'list-attached-role-policies', '--role-name', $RoleName, '--output', 'json') | ConvertFrom-Json).AttachedPolicies)
    if (@($inline | Where-Object { $_ -ne $PolicyName }).Count -gt 0 -or $attached.Count -gt 0)
    {
        throw "実行ロール $RoleName に管理外のポリシーがあります。手動で確認してください。"
    }
}

$existingDocument = Invoke-AwsCli -Arguments @(
    'ssm', 'get-document', '--region', $Region, '--name', $DocumentName,
    '--document-version', '$LATEST', '--document-format', 'JSON', '--output', 'json'
) -AbsentErrorCode 'InvalidDocument'
if ($null -ne $existingDocument -and ($existingDocument | ConvertFrom-Json).DocumentType -ne 'Automation')
{
    throw "SSM 文書 $DocumentName は Automation ではありません。"
}

if ($null -eq $existingRole)
{
    if ($PSCmdlet.ShouldProcess($RoleName, 'SSM Automation 実行ロールを作成'))
    {
        Invoke-AwsCliWithDocument -Arguments @(
            'iam', 'create-role', '--role-name', $RoleName,
            '--description', 'squirrel-notifier desktop E2E fixed launch automation (#380)'
        ) -OptionName '--assume-role-policy-document' -Document $trustPolicy | Out-Null
    }
}
elseif ($PSCmdlet.ShouldProcess($RoleName, '信頼ポリシーを更新'))
{
    Invoke-AwsCliWithDocument -Arguments @('iam', 'update-assume-role-policy', '--role-name', $RoleName) `
        -OptionName '--policy-document' -Document $trustPolicy | Out-Null
}

if ($PSCmdlet.ShouldProcess($RoleName, "inline policy $PolicyName を適用"))
{
    Invoke-AwsCliWithDocument -Arguments @(
        'iam', 'put-role-policy', '--role-name', $RoleName, '--policy-name', $PolicyName
    ) -OptionName '--policy-document' -Document $permissionPolicy | Out-Null
}

$content = ConvertTo-Json -InputObject $document -Depth 20 -Compress
$documentVersion = if ($null -ne $existingDocument) { ($existingDocument | ConvertFrom-Json).DocumentVersion } else { $null }
if ($null -eq $existingDocument)
{
    if ($PSCmdlet.ShouldProcess($DocumentName, 'Automation 文書を作成'))
    {
        $created = Invoke-AwsCliWithDocument -Arguments @(
            'ssm', 'create-document', '--region', $Region, '--name', $DocumentName,
            '--document-type', 'Automation', '--document-format', 'JSON', '--output', 'json'
        ) -OptionName '--content' -Document $document | ConvertFrom-Json
        $documentVersion = $created.DocumentDescription.DocumentVersion
    }
}
elseif ((ConvertTo-Json -InputObject ((($existingDocument | ConvertFrom-Json).Content) | ConvertFrom-Json -AsHashtable) -Depth 20 -Compress) -cne $content)
{
    if ($PSCmdlet.ShouldProcess($DocumentName, 'Automation 文書の新 version を作成'))
    {
        $updated = Invoke-AwsCliWithDocument -Arguments @(
            'ssm', 'update-document', '--region', $Region, '--name', $DocumentName,
            '--document-format', 'JSON', '--output', 'json'
        ) -OptionName '--content' -Document $document | ConvertFrom-Json
        $documentVersion = $updated.DocumentDescription.DocumentVersion
    }
}

[pscustomobject]@{
    schemaVersion         = 1
    accountId             = $accountId
    region                = $Region
    roleArn               = $roleArn
    documentName          = $DocumentName
    documentVersion       = $documentVersion
    launchTemplateId      = $template.LaunchTemplateId
    launchTemplateVersion = $template.VersionNumber
    rootVolume            = [ordered]@{
        size       = [int]$rootVolume.VolumeSize
        type       = $rootVolume.VolumeType
        iops       = $rootIops
        throughput = $rootThroughput
    }
} | ConvertTo-Json -Depth 4
