# Pester v5 tests for Initialize-DesktopRunnerLaunchTemplate.ps1
# 書き込みの条件を固定する（#380）。
# - タグの無い AMI、自アカウント所有でない AMI では何も書き込まずに止まる
# - 既存の Security Group に inbound があれば Launch Template を変更せずに止まる
# - default version が期待値と一致していれば新しい version を作らない。異なれば作って default にする
# aws CLI は、呼び出しを記録して状態に応じた応答を返す関数で置き換える。

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'Initialize-DesktopRunnerLaunchTemplate.ps1'
    Import-Module (Join-Path $PSScriptRoot 'DesktopRunnerImage.psm1') -Force
    $script:Tag = Get-DesktopRunnerResourceTag

    # 関数はコマンド解決で外部実行ファイルより優先されるため、スクリプト内の `& aws` はこちらを呼ぶ。
    function global:aws
    {
        $operation = "$($args[0]) $($args[1])"
        $global:FakeAwsCalls.Add($operation)
        $global:LASTEXITCODE = 0
        $fake = $global:FakeAwsState

        switch ($operation)
        {
            'ec2 describe-images'
            {
                $images = if ($fake.ImageOwned) { @([ordered]@{ ImageId = 'ami-0123456789abcdef0'; State = 'available'; Tags = $fake.ImageTags }) } else { @() }
                return (ConvertTo-Json -InputObject ([ordered]@{ Images = $images }) -Depth 10)
            }
            'ec2 describe-subnets' { return 'vpc-0123456789abcdef0' }
            'ec2 describe-security-groups'
            {
                $groups = if ($fake.SecurityGroupExists) { @([ordered]@{ GroupId = 'sg-0123456789abcdef0'; IpPermissions = $fake.SecurityGroupIngress }) } else { @() }
                return (ConvertTo-Json -InputObject ([ordered]@{ SecurityGroups = $groups }) -Depth 10)
            }
            'ec2 create-security-group' { return 'sg-0123456789abcdef0' }
            'ec2 describe-launch-templates'
            {
                if ($fake.LaunchTemplateData)
                {
                    return 'lt-0123456789abcdef0'
                }

                $global:LASTEXITCODE = 254
                return 'InvalidLaunchTemplateName.NotFoundException'
            }
            'ec2 describe-launch-template-versions'
            {
                $version = [ordered]@{ VersionNumber = 3; LaunchTemplateData = $fake.LaunchTemplateData }
                return (ConvertTo-Json -InputObject ([ordered]@{ LaunchTemplateVersions = @($version) }) -Depth 10)
            }
            'ec2 create-launch-template' { return 'lt-0123456789abcdef0' }
            'ec2 create-launch-template-version' { return '4' }
            default { return '' }
        }
    }

    function Invoke-InitializeScript
    {
        & $script:ScriptPath -ImageId 'ami-0123456789abcdef0' -SubnetId 'subnet-0123456789abcdef0' | ConvertFrom-Json
    }

    function New-FakeState
    {
        return @{
            ImageOwned           = $true
            ImageTags            = @([ordered]@{ Key = $script:Tag.Key; Value = $script:Tag.ImageValue })
            SecurityGroupExists  = $true
            SecurityGroupIngress = @()
            LaunchTemplateData   = $null
        }
    }

    $script:DesiredData = New-DesktopRunnerLaunchTemplateData `
        -ImageId 'ami-0123456789abcdef0' `
        -InstanceType 'm7i.xlarge' `
        -InstanceProfileName 'SquirrelNotifierDesktopE2ERunner' `
        -SubnetId 'subnet-0123456789abcdef0' `
        -SecurityGroupId 'sg-0123456789abcdef0'

    $script:WriteOperations = @(
        'ec2 create-security-group',
        'ec2 create-launch-template',
        'ec2 create-launch-template-version',
        'ec2 modify-launch-template'
    )
}

AfterAll {
    Remove-Item -Path 'function:global:aws' -ErrorAction SilentlyContinue
    Remove-Variable -Name 'FakeAwsCalls', 'FakeAwsState' -Scope Global -ErrorAction SilentlyContinue
}

Describe 'Initialize-DesktopRunnerLaunchTemplate.ps1' {
    BeforeEach {
        $global:FakeAwsCalls = [System.Collections.Generic.List[string]]::new()
        $global:FakeAwsState = New-FakeState
    }

    It '<Case> AMI では何も書き込まずに止まる' -ForEach @(
        @{ Case = 'タグの無い'; Setup = { param($s) $s.ImageTags = @() }; Message = '*squirrel-notifier:desktop-e2e=runner-image*' }
        @{ Case = '使い捨て instance 用のタグが付いた'; Setup = { param($s) $s.ImageTags = @([ordered]@{ Key = 'squirrel-notifier:desktop-e2e'; Value = 'ephemeral-runner' }) }; Message = '*New-DesktopRunnerImage.ps1*' }
        @{ Case = '自アカウント所有でない'; Setup = { param($s) $s.ImageOwned = $false }; Message = '*自アカウント*' }
    ) {
        & $Setup $global:FakeAwsState

        { Invoke-InitializeScript } | Should -Throw $Message

        @($global:FakeAwsCalls | Where-Object { $_ -in $script:WriteOperations }) | Should -BeNullOrEmpty
    }

    It '既存の Security Group に inbound があれば Launch Template を変更せずに止まる' {
        $global:FakeAwsState.SecurityGroupIngress = @([ordered]@{ IpProtocol = 'tcp'; FromPort = 3389; ToPort = 3389 })

        { Invoke-InitializeScript } | Should -Throw '*inbound*'

        @($global:FakeAwsCalls | Where-Object { $_ -in $script:WriteOperations }) | Should -BeNullOrEmpty
    }

    It 'Security Group が無ければ作成し、Launch Template も無ければ version 1 で作成する' {
        $global:FakeAwsState.SecurityGroupExists = $false

        $result = Invoke-InitializeScript

        $global:FakeAwsCalls | Should -Contain 'ec2 create-security-group'
        $global:FakeAwsCalls | Should -Contain 'ec2 create-launch-template'
        $result.defaultVersion | Should -Be 1
        $result.securityGroupId | Should -Be 'sg-0123456789abcdef0'
    }

    It 'default version が期待値と一致していれば新しい version を作らない' {
        $global:FakeAwsState.LaunchTemplateData = $script:DesiredData

        $result = Invoke-InitializeScript

        $global:FakeAwsCalls | Should -Not -Contain 'ec2 create-launch-template-version'
        $global:FakeAwsCalls | Should -Not -Contain 'ec2 modify-launch-template'
        $result.defaultVersion | Should -Be 3
    }

    It 'default version が異なれば新しい version を作ってから default にする' {
        $current = ConvertTo-Json -InputObject $script:DesiredData -Depth 10 | ConvertFrom-Json -AsHashtable
        $current.ImageId = 'ami-0fedcba9876543210'
        $global:FakeAwsState.LaunchTemplateData = $current

        $result = Invoke-InitializeScript

        $calls = @($global:FakeAwsCalls)
        $calls.IndexOf('ec2 create-launch-template-version') | Should -BeGreaterThan -1
        $calls.IndexOf('ec2 modify-launch-template') | Should -BeGreaterThan $calls.IndexOf('ec2 create-launch-template-version')
        $result.defaultVersion | Should -Be 4
    }
}
