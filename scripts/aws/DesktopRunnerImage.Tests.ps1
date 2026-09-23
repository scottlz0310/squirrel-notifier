# Pester v5 tests for DesktopRunnerImage.psm1
# 使い捨て desktop E2E runner の AMI と Launch Template の契約を固定する（#380）。
# - AMI と snapshot の両方に、OIDC ロールが条件にするタグを付ける
# - Launch Template から起動した instance・volume・ENI に、terminate の条件にするタグを付ける
# - instance 内から shutdown しても EBS を残さない。IMDSv2 を必須にする。UserData を持たない
# - 既存の Launch Template の比較はプロパティの順序だけを無視する
# 実際に aws CLI へ渡る形で検証するため、JSON へ変換して読み戻したものを対象にする。

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot 'DesktopRunnerImage.psm1') -Force

    $script:Tag = Get-DesktopRunnerResourceTag
    $script:TemplateArgs = @{
        ImageId             = 'ami-0123456789abcdef0'
        InstanceType        = 'm7i.xlarge'
        InstanceProfileName = 'SquirrelNotifierDesktopE2ERunner'
        SubnetId            = 'subnet-0123456789abcdef0'
        SecurityGroupId     = 'sg-0123456789abcdef0'
    }

    function ConvertTo-RoundTrip
    {
        param($Value)

        return (ConvertTo-Json -InputObject $Value -Depth 10 | ConvertFrom-Json)
    }

    function Get-TagValue
    {
        param(
            $Tags,
            [string]$Key
        )

        return @($Tags | Where-Object { $_.Key -eq $Key } | ForEach-Object { $_.Value })
    }

    $script:TemplateData = ConvertTo-RoundTrip (New-DesktopRunnerLaunchTemplateData @script:TemplateArgs)
}

Describe 'Get-DesktopRunnerResourceTag' {
    It 'AMI と使い捨て instance で異なる値を使う（AMI 用のタグで instance を terminate できないようにする）' {
        $script:Tag.ImageValue | Should -Not -Be $script:Tag.EphemeralInstanceValue
    }
}

Describe 'New-DesktopRunnerImageTagSpecification' {
    BeforeAll {
        $script:ImageTags = @(ConvertTo-RoundTrip (New-DesktopRunnerImageTagSpecification -ImageName 'squirrel-notifier-desktop-e2e-runner-20260923-000000' -SourceInstanceId 'i-0123456789abcdef0'))
    }

    It 'image と snapshot の両方にタグを付ける' {
        @($script:ImageTags.ResourceType) | Should -Be @('image', 'snapshot')
    }

    It '<ResourceType> に AMI 用のタグが付く' -ForEach @(
        @{ ResourceType = 'image' }
        @{ ResourceType = 'snapshot' }
    ) {
        $specification = $script:ImageTags | Where-Object { $_.ResourceType -eq $ResourceType }

        Get-TagValue -Tags $specification.Tags -Key $script:Tag.Key | Should -Be @($script:Tag.ImageValue)
        Get-TagValue -Tags $specification.Tags -Key 'squirrel-notifier:source-instance' | Should -Be @('i-0123456789abcdef0')
    }

    It '<Name> に <Value> を渡すと拒否する' -ForEach @(
        @{ Name = 'ImageName'; Value = 'a' }
        @{ Name = 'ImageName'; Value = 'name{with}braces' }
        @{ Name = 'InstanceId'; Value = '*' }
    ) {
        $tagArgs = @{ ImageName = 'squirrel-notifier-desktop-e2e-runner'; SourceInstanceId = 'i-0123456789abcdef0' }
        $key = if ($Name -eq 'InstanceId') { 'SourceInstanceId' } else { $Name }
        $tagArgs[$key] = $Value

        { New-DesktopRunnerImageTagSpecification @tagArgs } | Should -Throw "*$Name*"
    }
}

Describe 'New-DesktopRunnerLaunchTemplateData' {
    It 'instance 内から shutdown したときは terminate する' {
        $script:TemplateData.InstanceInitiatedShutdownBehavior | Should -Be 'terminate'
    }

    It 'IMDSv2 を必須にする' {
        $script:TemplateData.MetadataOptions.HttpTokens | Should -Be 'required'
    }

    It 'UserData を持たない（LaunchTemplateData は Describe で読めるため資格情報の経路にしない）' {
        $script:TemplateData.PSObject.Properties.Name | Should -Not -Contain 'UserData'
    }

    It '指定した subnet と Security Group の ENI を 1 つだけ持ち、public IP を付ける' {
        $interfaces = @($script:TemplateData.NetworkInterfaces)

        $interfaces.Count | Should -Be 1
        $interfaces[0].SubnetId | Should -Be 'subnet-0123456789abcdef0'
        @($interfaces[0].Groups) | Should -Be @('sg-0123456789abcdef0')
        $interfaces[0].AssociatePublicIpAddress | Should -BeTrue
        $interfaces[0].DeleteOnTermination | Should -BeTrue
    }

    It 'runner の instance profile を名前で指定する' {
        $script:TemplateData.IamInstanceProfile.Name | Should -Be 'SquirrelNotifierDesktopE2ERunner'
    }

    It '<ResourceType> に使い捨て instance 用のタグが付く' -ForEach @(
        @{ ResourceType = 'instance' }
        @{ ResourceType = 'volume' }
        @{ ResourceType = 'network-interface' }
    ) {
        $specification = @($script:TemplateData.TagSpecifications | Where-Object { $_.ResourceType -eq $ResourceType })

        $specification.Count | Should -Be 1
        Get-TagValue -Tags $specification[0].Tags -Key $script:Tag.Key | Should -Be @($script:Tag.EphemeralInstanceValue)
    }

    It '<Name> に <Value> を渡すと拒否する' -ForEach @(
        @{ Name = 'ImageId'; Value = 'ami-*' }
        @{ Name = 'ImageId'; Value = 'resolve:ssm:/parameter' }
        @{ Name = 'SubnetId'; Value = 'subnet-*' }
        @{ Name = 'SecurityGroupId'; Value = 'launch-wizard-1' }
        @{ Name = 'InstanceType'; Value = '*' }
        @{ Name = 'InstanceProfileName'; Value = 'path/profile' }
    ) {
        $templateArgs = $script:TemplateArgs.Clone()
        $templateArgs[$Name] = $Value

        { New-DesktopRunnerLaunchTemplateData @templateArgs } | Should -Throw "*$Name*"
    }
}

Describe 'Test-DesktopRunnerLaunchTemplateDataEqual' {
    BeforeAll {
        $script:Desired = New-DesktopRunnerLaunchTemplateData @script:TemplateArgs
    }

    It 'プロパティの順序だけが異なる場合は一致とみなす' {
        $json = ConvertTo-Json -InputObject $script:Desired -Depth 10 | ConvertFrom-Json -AsHashtable
        $reordered = [ordered]@{}
        foreach ($key in @($json.Keys | Sort-Object -Descending))
        {
            $reordered[$key] = $json[$key]
        }

        Test-DesktopRunnerLaunchTemplateDataEqual -Current (ConvertTo-RoundTrip $reordered) -Desired $script:Desired | Should -BeTrue
    }

    It '<Case> は不一致とみなす' -ForEach @(
        @{ Case = 'AMI の変更'; Mutate = { param($d) $d.ImageId = 'ami-0fedcba9876543210' } }
        @{ Case = 'AWS が補った項目'; Mutate = { param($d) $d.EbsOptimized = $true } }
        @{ Case = 'タグの欠落'; Mutate = { param($d) $d.TagSpecifications = @($d.TagSpecifications | Select-Object -First 2) } }
    ) {
        $current = ConvertTo-Json -InputObject $script:Desired -Depth 10 | ConvertFrom-Json -AsHashtable
        & $Mutate $current

        Test-DesktopRunnerLaunchTemplateDataEqual -Current (ConvertTo-RoundTrip $current) -Desired $script:Desired | Should -BeFalse
    }
}
