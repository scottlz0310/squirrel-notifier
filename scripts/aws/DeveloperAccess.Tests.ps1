# Pester v5 tests for DeveloperAccess.psm1
# 開発者用 IAM ポリシーの権限境界を固定する（#405）。
# - 変更系の操作を "*" に広げない（runner instance / Run Command ドキュメント / parameter prefix に限定）
# - default の IAM ユーザーに IAM の操作を持たせない（アクセスキーを自分で作れないようにする）
# - Admin ロールは指定した IAM ユーザーだけを信頼する
# 実際に IAM へ渡る形で検証するため、JSON へ変換して読み戻したものを対象にする。

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot 'DeveloperAccess.psm1') -Force

    $script:PolicyArgs = @{
        AccountId       = '123456789012'
        Region          = 'us-east-1'
        InstanceId      = 'i-0123456789abcdef0'
        ParameterPrefix = '/squirrel-notifier/desktop-e2e'
        AdminRoleName   = 'SquirrelNotifierAdmin'
    }

    function ConvertTo-PolicyDocument
    {
        param($Policy)

        return ($Policy | ConvertTo-Json -Depth 10 | ConvertFrom-Json)
    }

    function Get-StatementForAction
    {
        param(
            $Document,
            [string]$Action
        )

        return @($Document.Statement | Where-Object { @($_.Action) -contains $Action })
    }

    $script:Operator = ConvertTo-PolicyDocument (New-DeveloperOperatorPolicy @script:PolicyArgs)
    $script:AllActions = @($script:Operator.Statement | ForEach-Object { @($_.Action) })
}

Describe 'New-DeveloperOperatorPolicy' {
    It '変更系の <Action> は <Expected> だけに限定される' -ForEach @(
        @{ Action = 'ec2:StartInstances'; Expected = @('arn:aws:ec2:us-east-1:123456789012:instance/i-0123456789abcdef0') }
        @{ Action = 'ec2:StopInstances'; Expected = @('arn:aws:ec2:us-east-1:123456789012:instance/i-0123456789abcdef0') }
        @{ Action = 'ssm:SendCommand'; Expected = @('arn:aws:ec2:us-east-1:123456789012:instance/i-0123456789abcdef0', 'arn:aws:ssm:us-east-1::document/AWS-RunPowerShellScript') }
        @{ Action = 'ssm:PutParameter'; Expected = @('arn:aws:ssm:us-east-1:123456789012:parameter/squirrel-notifier/desktop-e2e/*') }
        @{ Action = 'sts:AssumeRole'; Expected = @('arn:aws:iam::123456789012:role/SquirrelNotifierAdmin') }
    ) {
        $statements = Get-StatementForAction -Document $script:Operator -Action $Action

        $statements.Count | Should -Be 1
        (@($statements[0].Resource) -join ',') | Should -Be ($Expected -join ',')
    }

    It '<Action> は条件なしで許可されない' -ForEach @(
        @{ Action = 'kms:Decrypt' }
        @{ Action = 'kms:Encrypt' }
    ) {
        $statement = (Get-StatementForAction -Document $script:Operator -Action $Action)[0]

        $statement.Condition.StringEquals.'kms:ViaService' | Should -Be 'ssm.us-east-1.amazonaws.com'
    }

    It 'IAM の操作を含まない' {
        @($script:AllActions | Where-Object { $_ -like 'iam:*' }) | Should -BeNullOrEmpty
    }

    It 'ワイルドカードを含むアクションを持たない' {
        @($script:AllActions | Where-Object { $_.Contains('*') }) | Should -BeNullOrEmpty
    }

    It 'Deny を含まない（許可の範囲だけで境界を表す）' {
        @($script:Operator.Statement | Where-Object { $_.Effect -ne 'Allow' }) | Should -BeNullOrEmpty
    }

    It 'parameter prefix の末尾が <Prefix> でも ARN の区切りを二重にしない' -ForEach @(
        @{ Prefix = '/squirrel-notifier/desktop-e2e' }
        @{ Prefix = '/squirrel-notifier/desktop-e2e/' }
    ) {
        $policyArgs = $script:PolicyArgs.Clone()
        $policyArgs.ParameterPrefix = $Prefix
        $document = ConvertTo-PolicyDocument (New-DeveloperOperatorPolicy @policyArgs)

        $statement = (Get-StatementForAction -Document $document -Action 'ssm:GetParameter')[0]
        $statement.Resource | Should -Be 'arn:aws:ssm:us-east-1:123456789012:parameter/squirrel-notifier/desktop-e2e/*'
    }
}

Describe 'New-DeveloperAdminTrustPolicy' {
    BeforeAll {
        $script:Trust = ConvertTo-PolicyDocument (New-DeveloperAdminTrustPolicy -AccountId '123456789012' -UserName 'developer')
    }

    It '指定した IAM ユーザーだけを信頼する' {
        $statements = @($script:Trust.Statement)

        $statements.Count | Should -Be 1
        $statements[0].Effect | Should -Be 'Allow'
        $statements[0].Action | Should -Be 'sts:AssumeRole'
        (@($statements[0].Principal.AWS) -join ',') | Should -Be 'arn:aws:iam::123456789012:user/developer'
    }

    It 'サービスやフェデレーションの principal を含まない' {
        $principal = $script:Trust.Statement[0].Principal

        @($principal.PSObject.Properties.Name) | Should -Be @('AWS')
    }
}
