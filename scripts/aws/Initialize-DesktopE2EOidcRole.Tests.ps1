# Pester v5 tests for Initialize-DesktopE2EOidcRole.ps1
# 書き込みの条件と順序を固定する（#380）。
# - 入力値が不正、OIDC provider が無い、Launch Template が無い、存在確認が NotFound 以外で失敗した場合は
#   何も書き込まずに止まる
# - 既存ロールでは信頼ポリシーを inline policy より先に更新する
# - RunInstances の条件は Launch Template の default version から読む
# aws CLI は、呼び出しを記録して状態に応じた応答を返す関数で置き換える。

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'Initialize-DesktopE2EOidcRole.ps1'

    # 関数はコマンド解決で外部実行ファイルより優先されるため、スクリプト内の `& aws` はこちらを呼ぶ。
    function global:aws
    {
        $operation = "$($args[0]) $($args[1])"
        $global:FakeAwsCalls.Add($operation)
        $global:LASTEXITCODE = 0
        $fake = $global:FakeAwsState

        # put-role-policy に渡したポリシーを検証できるよう、file:// の中身を記録する。
        $fileArgument = @($args | Where-Object { "$_" -like 'file://*' }) | Select-Object -First 1
        if ($fileArgument)
        {
            $global:FakeAwsDocuments[$operation] = Get-Content -LiteralPath ("$fileArgument".Substring(7)) -Raw | ConvertFrom-Json
        }

        if ($operation -eq $fake.FailOn)
        {
            $global:LASTEXITCODE = 254
            return "An error occurred ($($fake.FailWith)) when calling the operation"
        }

        switch ($operation)
        {
            'sts get-caller-identity' { return '123456789012' }
            'iam get-open-id-connect-provider'
            {
                if ($fake.ProviderExists) { return 'token.actions.githubusercontent.com' }
                $global:LASTEXITCODE = 254
                return 'An error occurred (NoSuchEntity) when calling the GetOpenIDConnectProvider operation'
            }
            'ec2 describe-launch-template-versions'
            {
                if (-not $fake.LaunchTemplateExists)
                {
                    $global:LASTEXITCODE = 254
                    return 'An error occurred (InvalidLaunchTemplateName.NotFoundException) when calling the DescribeLaunchTemplateVersions operation'
                }

                $version = [ordered]@{
                    LaunchTemplateId   = 'lt-0123456789abcdef0'
                    VersionNumber      = 2
                    LaunchTemplateData = [ordered]@{
                        ImageId           = 'ami-0123456789abcdef0'
                        InstanceType      = 'm7i.2xlarge'
                        NetworkInterfaces = @([ordered]@{ SubnetId = 'subnet-0fedcba9876543210'; Groups = @('sg-0fedcba9876543210') })
                    }
                }
                return (ConvertTo-Json -InputObject ([ordered]@{ LaunchTemplateVersions = @($version) }) -Depth 10)
            }
            'ec2 describe-images'
            {
                # root 以外の EBS と instance store のマッピングも混ぜ、root device のものだけを使うことを確かめる。
                return (ConvertTo-Json -Depth 10 -InputObject ([ordered]@{
                            RootDeviceName      = $fake.RootDeviceName
                            BlockDeviceMappings = @(
                                [ordered]@{ DeviceName = '/dev/sdf'; Ebs = [ordered]@{ VolumeSize = 500; VolumeType = 'io2'; Iops = 64000 } }
                                [ordered]@{ DeviceName = '/dev/sda1'; Ebs = [ordered]@{ VolumeSize = 40; VolumeType = 'gp3'; Iops = 3000; Throughput = 125 } }
                                [ordered]@{ DeviceName = 'xvdca'; VirtualName = 'ephemeral0' }
                            )
                        }))
            }
            'iam get-role'
            {
                if ($fake.RoleExists) { return 'GitHubActionsSquirrelNotifierDesktopE2E' }
                $global:LASTEXITCODE = 254
                return 'An error occurred (NoSuchEntity) when calling the GetRole operation'
            }
            'iam list-role-policies' { return (ConvertTo-Json -InputObject ([ordered]@{ PolicyNames = $fake.InlinePolicies })) }
            'iam list-attached-role-policies' { return (ConvertTo-Json -InputObject ([ordered]@{ AttachedPolicies = @() })) }
            default { return '' }
        }
    }

    function Invoke-InitializeScript
    {
        param([hashtable]$Overrides = @{})

        $arguments = @{ LegacyInstanceId = 'i-0123456789abcdef0' }
        foreach ($key in $Overrides.Keys)
        {
            $arguments[$key] = $Overrides[$key]
        }

        & $script:ScriptPath @arguments 3>$null | ConvertFrom-Json
    }

    $script:WriteOperations = @(
        'iam create-role',
        'iam update-assume-role-policy',
        'iam put-role-policy'
    )
}

AfterAll {
    Remove-Item -Path 'function:global:aws' -ErrorAction SilentlyContinue
    Remove-Variable -Name 'FakeAwsCalls', 'FakeAwsState', 'FakeAwsDocuments' -Scope Global -ErrorAction SilentlyContinue
}

Describe 'Initialize-DesktopE2EOidcRole.ps1' {
    BeforeEach {
        $global:FakeAwsCalls = [System.Collections.Generic.List[string]]::new()
        $global:FakeAwsDocuments = @{}
        $global:FakeAwsState = @{
            ProviderExists       = $true
            LaunchTemplateExists = $true
            RoleExists           = $true
            InlinePolicies       = @('SquirrelNotifierDesktopE2EInstanceControl')
            RootDeviceName       = '/dev/sda1'
            FailOn               = $null
            FailWith             = $null
        }
    }

    It '<Case> では何も書き込まずに止まる' -ForEach @(
        @{ Case = '不正な LegacyInstanceId'; Setup = { param($s) }; Overrides = @{ LegacyInstanceId = 'i-*' }; Message = '*LegacyInstanceId*' }
        @{ Case = '不正な Environment'; Setup = { param($s) }; Overrides = @{ Environment = '*' }; Message = '*Environment*' }
        @{ Case = 'OIDC provider が無い状態'; Setup = { param($s) $s.ProviderExists = $false }; Overrides = @{}; Message = '*OIDC provider*' }
        @{ Case = 'Launch Template が無い状態'; Setup = { param($s) $s.LaunchTemplateExists = $false }; Overrides = @{}; Message = '*Initialize-DesktopRunnerLaunchTemplate.ps1*' }
        @{ Case = 'ロールの存在確認が権限不足で失敗した状態'; Setup = { param($s) $s.FailOn = 'iam get-role'; $s.FailWith = 'AccessDenied' }; Overrides = @{}; Message = '*AccessDenied*' }
        @{ Case = 'AMI に root device の EBS マッピングが無い状態'; Setup = { param($s) $s.RootDeviceName = '/dev/xvda' }; Overrides = @{}; Message = '*root device*' }
        @{ Case = 'AMI の読み取りが権限不足で失敗した状態'; Setup = { param($s) $s.FailOn = 'ec2 describe-images'; $s.FailWith = 'UnauthorizedOperation' }; Overrides = @{}; Message = '*UnauthorizedOperation*' }
    ) {
        & $Setup $global:FakeAwsState

        { Invoke-InitializeScript -Overrides $Overrides } | Should -Throw $Message

        @($global:FakeAwsCalls | Where-Object { $_ -in $script:WriteOperations }) | Should -BeNullOrEmpty
    }

    It '既存ロールでは信頼ポリシーを inline policy より先に更新する' {
        Invoke-InitializeScript | Out-Null

        $calls = @($global:FakeAwsCalls)
        $calls | Should -Not -Contain 'iam create-role'
        $calls.IndexOf('iam update-assume-role-policy') | Should -BeGreaterThan -1
        $calls.IndexOf('iam put-role-policy') | Should -BeGreaterThan $calls.IndexOf('iam update-assume-role-policy')
    }

    It '信頼ポリシーの更新に失敗したら inline policy を更新しない' {
        $global:FakeAwsState.FailOn = 'iam update-assume-role-policy'
        $global:FakeAwsState.FailWith = 'MalformedPolicyDocument'

        { Invoke-InitializeScript } | Should -Throw '*MalformedPolicyDocument*'

        $global:FakeAwsCalls | Should -Not -Contain 'iam put-role-policy'
    }

    It 'ロールが無ければ作成してから inline policy を適用する' {
        $global:FakeAwsState.RoleExists = $false

        Invoke-InitializeScript | Out-Null

        $calls = @($global:FakeAwsCalls)
        $calls.IndexOf('iam create-role') | Should -BeGreaterThan -1
        $calls.IndexOf('iam put-role-policy') | Should -BeGreaterThan $calls.IndexOf('iam create-role')
        $calls | Should -Not -Contain 'iam list-role-policies'
    }

    It 'RunInstances の条件は Launch Template の default version から読む' {
        $result = Invoke-InitializeScript

        $policy = $global:FakeAwsDocuments['iam put-role-policy']
        $instance = @($policy.Statement | Where-Object { $_.Sid -eq 'RunEphemeralRunnerInstance' })[0]
        $instance.Condition.StringEquals.'ec2:InstanceType' | Should -Be 'm7i.2xlarge'
        $instance.Condition.ArnEquals.'ec2:LaunchTemplate' | Should -Be 'arn:aws:ec2:us-east-1:123456789012:launch-template/lt-0123456789abcdef0'
        $network = @($policy.Statement | Where-Object { $_.Sid -eq 'RunEphemeralRunnerResources' })[0]
        @($network.Resource) | Should -Contain 'arn:aws:ec2:us-east-1:123456789012:subnet/subnet-0fedcba9876543210'
        @($network.Resource) | Should -Contain 'arn:aws:ec2:us-east-1:123456789012:security-group/sg-0fedcba9876543210'
        $result.launchTemplateVersion | Should -Be 2
    }

    It 'volume の上限は Launch Template の AMI の root device のマッピングから読む' {
        $result = Invoke-InitializeScript

        $policy = $global:FakeAwsDocuments['iam put-role-policy']
        $volume = @($policy.Statement | Where-Object { $_.Sid -eq 'RunEphemeralRunnerRootVolume' })[0]
        $volume.Condition.NumericLessThanEquals.'ec2:VolumeSize' | Should -Be 40
        $volume.Condition.StringEquals.'ec2:VolumeType' | Should -Be 'gp3'
        $volume.Condition.NumericLessThanEqualsIfExists.'ec2:VolumeIops' | Should -Be 3000
        $volume.Condition.NumericLessThanEqualsIfExists.'ec2:VolumeThroughput' | Should -Be 125
        @($global:FakeAwsCalls) | Should -Contain 'ec2 describe-images'
        $result.rootVolume.imageId | Should -Be 'ami-0123456789abcdef0'
        $result.rootVolume.deviceName | Should -Be '/dev/sda1'
    }

    It '管理外の inline policy を削除せずに出力へ列挙する' {
        $global:FakeAwsState.InlinePolicies = @('SquirrelNotifierDesktopE2EInstanceControl', 'LegacyExtra')

        $result = Invoke-InitializeScript

        @($result.unmanagedInlinePolicies) | Should -Be @('LegacyExtra')
        @($global:FakeAwsCalls | Where-Object { $_ -like 'iam delete-*' }) | Should -BeNullOrEmpty
    }
}
