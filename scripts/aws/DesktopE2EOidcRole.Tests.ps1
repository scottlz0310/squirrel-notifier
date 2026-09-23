# Pester v5 tests for DesktopE2EOidcRole.psm1
# desktop E2E workflow の OIDC ロールの権限境界を固定する（#380）。
# - 信頼は指定 repository の指定 environment の job だけ。前方一致（StringLike / wildcard）を使わない
# - 既存 instance は Start / Stop だけで、terminate できない
# - 使い捨て instance は Launch Template 経由でだけ起動でき、ephemeral-runner タグのものだけ terminate できる
# - 起動元は自アカウント所有で runner-image タグの付いた AMI だけ
# - 変更系の操作を "*" に広げない。ARN へ埋め込む値に wildcard を受け付けない
# 実際に IAM へ渡る形で検証するため、JSON へ変換して読み戻したものを対象にする。

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot 'DesktopE2EOidcRole.psm1') -Force
    Import-Module (Join-Path $PSScriptRoot 'DesktopRunnerImage.psm1') -Force

    $script:Tag = Get-DesktopRunnerResourceTag
    $script:PolicyArgs = @{
        AccountId          = '123456789012'
        Region             = 'us-east-1'
        LegacyInstanceId   = 'i-0123456789abcdef0'
        LaunchTemplateId   = 'lt-0123456789abcdef0'
        InstanceType       = 'm7i.xlarge'
        SubnetId           = 'subnet-0123456789abcdef0'
        SecurityGroupId    = 'sg-0123456789abcdef0'
        RunnerRoleName     = 'SquirrelNotifierDesktopE2ERunner'
        JitParameterPrefix = '/squirrel-notifier/desktop-e2e/jit'
    }

    function ConvertTo-PolicyDocument
    {
        param($Policy)

        return (ConvertTo-Json -InputObject $Policy -Depth 10 | ConvertFrom-Json)
    }

    function Get-StatementForAction
    {
        param(
            $Document,
            [string]$Action
        )

        return @($Document.Statement | Where-Object { @($_.Action) -contains $Action })
    }

    $script:Policy = ConvertTo-PolicyDocument (New-DesktopE2EOidcPermissionPolicy @script:PolicyArgs)
    $script:AllActions = @($script:Policy.Statement | ForEach-Object { @($_.Action) })
    $script:Trust = ConvertTo-PolicyDocument (New-DesktopE2EOidcTrustPolicy -AccountId '123456789012' -Repository 'scottlz0310/squirrel-notifier' -Environment 'desktop-e2e')
}

Describe 'New-DesktopE2EOidcTrustPolicy' {
    It 'GitHub の OIDC provider だけを AssumeRoleWithWebIdentity で信頼する' {
        $statements = @($script:Trust.Statement)

        $statements.Count | Should -Be 1
        $statements[0].Action | Should -Be 'sts:AssumeRoleWithWebIdentity'
        @($statements[0].Principal.PSObject.Properties.Name) | Should -Be @('Federated')
        $statements[0].Principal.Federated | Should -Be 'arn:aws:iam::123456789012:oidc-provider/token.actions.githubusercontent.com'
    }

    It 'sub を environment に完全一致で固定し、前方一致を使わない' {
        $condition = $script:Trust.Statement[0].Condition

        @($condition.PSObject.Properties.Name) | Should -Be @('StringEquals')
        $condition.StringEquals.'token.actions.githubusercontent.com:sub' | Should -Be 'repo:scottlz0310/squirrel-notifier:environment:desktop-e2e'
        $condition.StringEquals.'token.actions.githubusercontent.com:aud' | Should -Be 'sts.amazonaws.com'
    }

    It '<Name> に <Value> を渡すと拒否する' -ForEach @(
        @{ Name = 'Repository'; Value = 'scottlz0310/*' }
        @{ Name = 'Repository'; Value = 'scottlz0310' }
        @{ Name = 'Environment'; Value = '*' }
        @{ Name = 'Environment'; Value = 'desktop-e2e:*' }
        @{ Name = 'AccountId'; Value = '*' }
    ) {
        $trustArgs = @{ AccountId = '123456789012'; Repository = 'scottlz0310/squirrel-notifier'; Environment = 'desktop-e2e' }
        $trustArgs[$Name] = $Value

        { New-DesktopE2EOidcTrustPolicy @trustArgs } | Should -Throw "*$Name*"
    }
}

Describe 'New-DesktopE2EOidcPermissionPolicy' {
    It '既存 instance の <Action> はその instance だけに限定される' -ForEach @(
        @{ Action = 'ec2:StartInstances' }
        @{ Action = 'ec2:StopInstances' }
    ) {
        $statements = Get-StatementForAction -Document $script:Policy -Action $Action

        $statements.Count | Should -Be 1
        $statements[0].Resource | Should -Be 'arn:aws:ec2:us-east-1:123456789012:instance/i-0123456789abcdef0'
    }

    It 'TerminateInstances は ephemeral-runner タグの付いた instance だけに限定される' {
        $statements = Get-StatementForAction -Document $script:Policy -Action 'ec2:TerminateInstances'

        $statements.Count | Should -Be 1
        $statements[0].Condition.StringEquals."aws:ResourceTag/$($script:Tag.Key)" | Should -Be $script:Tag.EphemeralInstanceValue
    }

    It 'CreateTags は RunInstances と同時のタグ付けだけに限定される（既存 instance に terminate 用のタグを付けられない）' {
        $statements = Get-StatementForAction -Document $script:Policy -Action 'ec2:CreateTags'

        $statements.Count | Should -Be 1
        $statements[0].Condition.StringEquals.'ec2:CreateAction' | Should -Be 'RunInstances'
    }

    It 'RunInstances の instance は Launch Template と instance type を条件にする' {
        $statement = @($script:Policy.Statement | Where-Object { $_.Sid -eq 'RunEphemeralRunnerInstance' })[0]

        $statement.Resource | Should -Be 'arn:aws:ec2:us-east-1:123456789012:instance/*'
        $statement.Condition.ArnEquals.'ec2:LaunchTemplate' | Should -Be 'arn:aws:ec2:us-east-1:123456789012:launch-template/lt-0123456789abcdef0'
        $statement.Condition.StringEquals.'ec2:InstanceType' | Should -Be 'm7i.xlarge'
    }

    It 'RunInstances の subnet と Security Group は Launch Template のものだけ' {
        $statement = @($script:Policy.Statement | Where-Object { $_.Sid -eq 'RunEphemeralRunnerResources' })[0]
        $resources = @($statement.Resource)

        $resources | Should -Contain 'arn:aws:ec2:us-east-1:123456789012:subnet/subnet-0123456789abcdef0'
        $resources | Should -Contain 'arn:aws:ec2:us-east-1:123456789012:security-group/sg-0123456789abcdef0'
        @($resources | Where-Object { $_ -match ':(subnet|security-group)/\*$' }) | Should -BeNullOrEmpty
        $statement.Condition.ArnEquals.'ec2:LaunchTemplate' | Should -Be 'arn:aws:ec2:us-east-1:123456789012:launch-template/lt-0123456789abcdef0'
    }

    It 'RunInstances の起動元 AMI は自アカウント所有で runner-image タグの付いたものだけ' {
        $statement = @($script:Policy.Statement | Where-Object { $_.Sid -eq 'RunFromRunnerImage' })[0]

        $statement.Condition.StringEquals.'ec2:Owner' | Should -Be '123456789012'
        $statement.Condition.StringEquals."aws:ResourceTag/$($script:Tag.Key)" | Should -Be $script:Tag.ImageValue
    }

    It 'RunInstances の各 statement は条件なしで許可されない' {
        $statements = Get-StatementForAction -Document $script:Policy -Action 'ec2:RunInstances'

        $statements.Count | Should -BeGreaterThan 0
        @($statements | Where-Object { $null -eq $_.PSObject.Properties['Condition'] }) | Should -BeNullOrEmpty
    }

    It 'PassRole は runner role を EC2 へ渡す場合だけ' {
        $statements = Get-StatementForAction -Document $script:Policy -Action 'iam:PassRole'

        $statements.Count | Should -Be 1
        $statements[0].Resource | Should -Be 'arn:aws:iam::123456789012:role/SquirrelNotifierDesktopE2ERunner'
        $statements[0].Condition.StringEquals.'iam:PassedToService' | Should -Be 'ec2.amazonaws.com'
    }

    It 'IAM の操作は PassRole だけ' {
        @($script:AllActions | Where-Object { $_ -like 'iam:*' }) | Should -Be @('iam:PassRole')
    }

    It 'JIT config の parameter は prefix 配下の Put / Delete だけ' {
        $statement = @($script:Policy.Statement | Where-Object { $_.Sid -eq 'WriteJitRunnerConfig' })[0]

        @($statement.Action) | Should -Be @('ssm:PutParameter', 'ssm:DeleteParameter')
        $statement.Resource | Should -Be 'arn:aws:ssm:us-east-1:123456789012:parameter/squirrel-notifier/desktop-e2e/jit/*'
    }

    It 'Resource "*" は Describe 系と SSM 経由の kms:Encrypt だけ' {
        $unscoped = @($script:Policy.Statement | Where-Object { @($_.Resource) -contains '*' })

        @($unscoped | ForEach-Object { @($_.Action) }) | Should -Be @('ec2:DescribeInstances', 'ec2:DescribeInstanceStatus', 'kms:Encrypt')
        $kms = @($unscoped | Where-Object { @($_.Action) -contains 'kms:Encrypt' })[0]
        $kms.Condition.StringEquals.'kms:ViaService' | Should -Be 'ssm.us-east-1.amazonaws.com'
    }

    It 'ワイルドカードを含むアクションを持たない' {
        @($script:AllActions | Where-Object { $_.Contains('*') }) | Should -BeNullOrEmpty
    }

    It '<Name> に <Value> を渡すと ARN を広げずに拒否する' -ForEach @(
        @{ Name = 'LegacyInstanceId'; Value = 'i-*' }
        @{ Name = 'LaunchTemplateId'; Value = 'lt-*' }
        @{ Name = 'SubnetId'; Value = 'subnet-*' }
        @{ Name = 'SecurityGroupId'; Value = '*' }
        @{ Name = 'InstanceType'; Value = 'm7i.*' }
        @{ Name = 'RunnerRoleName'; Value = '*' }
        @{ Name = 'JitParameterPrefix'; Value = '/' }
        @{ Name = 'JitParameterPrefix'; Value = '/squirrel-notifier' }
        @{ Name = 'Region'; Value = '*' }
        @{ Name = 'AccountId'; Value = '*' }
    ) {
        $policyArgs = $script:PolicyArgs.Clone()
        $policyArgs[$Name] = $Value

        { New-DesktopE2EOidcPermissionPolicy @policyArgs } | Should -Throw "*$Name*"
    }
}
