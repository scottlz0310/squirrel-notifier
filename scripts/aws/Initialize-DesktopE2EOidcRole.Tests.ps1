# Pester v5 tests for Initialize-DesktopE2EOidcRole.ps1
# 書き込みの条件と順序を固定する（#380）。
# - 入力値が不正、OIDC provider が無い、Launch Template が無い、存在確認が NotFound 以外で失敗した場合は
#   何も書き込まずに止まる
# - 既存ロールでは信頼ポリシーを inline policy より先に更新する
# - 固定 SSM 文書と version を確認し、OIDC からの直接起動は許可しない
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
                # instance store のマッピングはあり得るが、EBS は root だけにする。
                return (ConvertTo-Json -Depth 10 -InputObject ([ordered]@{
                            RootDeviceName      = $fake.RootDeviceName
                            BlockDeviceMappings = @(
                                [ordered]@{ DeviceName = '/dev/sda1'; Ebs = [ordered]@{ VolumeSize = 40; VolumeType = 'gp3'; Iops = 3000; Throughput = 125 } }
                                [ordered]@{ DeviceName = 'xvdca'; VirtualName = 'ephemeral0' }
                            )
                        }))
            }
            'ssm get-document'
            {
                $document = New-DesktopE2ELaunchAutomationDocument `
                    -RoleArn 'arn:aws:iam::123456789012:role/SquirrelNotifierDesktopE2EAutomation' `
                    -LaunchTemplateId 'lt-0123456789abcdef0' -LaunchTemplateVersion 2
                if ($fake.DocumentMismatch) { $document.mainSteps[0].inputs.MaxCount = 2 }
                return (ConvertTo-Json -Depth 20 -InputObject ([ordered]@{
                            DocumentType = 'Automation'
                            DocumentVersion = '1'
                            Content = ConvertTo-Json -InputObject $document -Depth 20 -Compress
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

        $arguments = @{ LegacyInstanceId = 'i-0123456789abcdef0'; AutomationDocumentVersion = '1' }
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
            DocumentMismatch     = $false
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
        @{ Case = 'Automation 文書の内容が異なる状態'; Setup = { param($s) $s.DocumentMismatch = $true }; Overrides = @{}; Message = '*固定起動仕様と一致しません*' }
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

    It '直接起動を外し、SSM 文書の確定 version に限定する' {
        $result = Invoke-InitializeScript

        $policy = $global:FakeAwsDocuments['iam put-role-policy']
        @($policy.Statement | Where-Object { $_.Action -eq 'ec2:RunInstances' -or $_.Action -eq 'ec2:CreateTags' }) | Should -BeNullOrEmpty
        $start = @($policy.Statement | Where-Object { $_.Sid -eq 'StartFixedLaunchAutomationDocument' })[0]
        $start.Resource | Should -Be 'arn:aws:ssm:us-east-1:123456789012:document/SquirrelNotifierDesktopE2ELaunch'
        $start.Condition.'ForAnyValue:StringEquals'.'ssm:DocumentVersion' | Should -Be @('1')
        $result.automationDocumentVersion | Should -Be '1'
        $result.launchTemplateVersion | Should -Be 2
    }

    It 'root volume の前提は Launch Template の AMI から読む' {
        $result = Invoke-InitializeScript

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
