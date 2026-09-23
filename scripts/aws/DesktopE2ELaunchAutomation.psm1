# 使い捨て desktop E2E instance の起動要求を SSM Automation に固定する（#380）。

Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'DesktopE2EOidcRole.psm1') -Force

function Assert-DesktopE2ELaunchStorage
{
    param(
        [Parameter(Mandatory)]$LaunchTemplateData,
        [Parameter(Mandatory)]$Image
    )

    $mappingProperty = $LaunchTemplateData.PSObject.Properties['BlockDeviceMappings']
    $mappings = @()
    if ($null -ne $mappingProperty)
    {
        $mappings = @($mappingProperty.Value | Where-Object { $null -ne $_ })
    }
    if ($mappings.Count -gt 0)
    {
        throw 'Launch Template に追加 block device mapping があります。固定起動文書には使えません。'
    }
    $userDataProperty = $LaunchTemplateData.PSObject.Properties['UserData']
    if ($null -ne $userDataProperty -and -not [string]::IsNullOrEmpty($userDataProperty.Value))
    {
        throw 'Launch Template に UserData があります。固定起動文書には使えません。'
    }

    $ebsMappings = @($Image.BlockDeviceMappings | Where-Object { $null -ne $_.PSObject.Properties['Ebs'] -and $null -ne $_.Ebs })
    if ($ebsMappings.Count -ne 1 -or $ebsMappings[0].DeviceName -cne $Image.RootDeviceName)
    {
        throw 'AMI の EBS mapping は root device 1 本だけである必要があります。'
    }
}

function New-DesktopE2ELaunchAutomationTrustPolicy
{
    param(
        [Parameter(Mandatory)]
        [ValidatePattern('^\d{12}$')]
        [string]$AccountId,

        [Parameter(Mandatory)]
        [ValidatePattern('^[a-z]{2}(-[a-z]+)+-\d{1,2}$')]
        [string]$Region
    )

    return [ordered]@{
        Version   = '2012-10-17'
        Statement = @(
            [ordered]@{
                Effect    = 'Allow'
                Principal = [ordered]@{ Service = 'ssm.amazonaws.com' }
                Action    = 'sts:AssumeRole'
                Condition = [ordered]@{
                    StringEquals = [ordered]@{ 'aws:SourceAccount' = $AccountId }
                    ArnLike      = [ordered]@{ 'aws:SourceArn' = "arn:aws:ssm:${Region}:${AccountId}:automation-execution/*" }
                }
            }
        )
    }
}

function New-DesktopE2ELaunchAutomationDocument
{
    param(
        [Parameter(Mandatory)]
        [ValidatePattern('^arn:aws:iam::\d{12}:role/[A-Za-z0-9+=,.@_-]{1,64}$')]
        [string]$RoleArn,

        [Parameter(Mandatory)]
        [ValidatePattern('^lt-[0-9a-f]{8,17}$')]
        [string]$LaunchTemplateId,

        [Parameter(Mandatory)]
        [ValidateRange(1, 10000)]
        [int]$LaunchTemplateVersion
    )

    return [ordered]@{
        schemaVersion = '0.3'
        description   = 'squirrel-notifier desktop E2E 用の使い捨て EC2 instance を固定した Launch Template version から 1 台起動する（#380）。'
        assumeRole    = $RoleArn
        mainSteps     = @(
            [ordered]@{
                name      = 'launchInstance'
                action    = 'aws:executeAwsApi'
                onFailure = 'Abort'
                inputs    = [ordered]@{
                    Service        = 'ec2'
                    Api            = 'RunInstances'
                    LaunchTemplate = [ordered]@{
                        LaunchTemplateId = $LaunchTemplateId
                        Version          = "$LaunchTemplateVersion"
                    }
                    MinCount       = 1
                    MaxCount       = 1
                }
                outputs   = @(
                    [ordered]@{
                        Name     = 'InstanceId'
                        Selector = '$.Instances[0].InstanceId'
                        Type     = 'String'
                    }
                )
            }
        )
        outputs       = @('launchInstance.InstanceId')
    }
}

function New-DesktopE2ELaunchAutomationPermissionPolicy
{
    param(
        [Parameter(Mandatory)]
        [hashtable]$RunnerPolicyArguments
    )

    $source = New-DesktopE2EOidcPermissionPolicy @RunnerPolicyArguments
    $launchSids = @(
        'RunEphemeralRunnerInstance',
        'RunEphemeralRunnerResources',
        'RunEphemeralRunnerRootVolume',
        'RunFromRunnerImage',
        'RunFromRunnerImageSnapshot',
        'TagEphemeralRunnerOnLaunch',
        'PassRunnerInstanceRole'
    )

    return [ordered]@{
        Version   = '2012-10-17'
        Statement = @($source.Statement | Where-Object { $_.Sid -in $launchSids })
    }
}

Export-ModuleMember -Function @(
    'Assert-DesktopE2ELaunchStorage',
    'New-DesktopE2ELaunchAutomationTrustPolicy',
    'New-DesktopE2ELaunchAutomationDocument',
    'New-DesktopE2ELaunchAutomationPermissionPolicy'
)
