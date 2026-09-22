# Pester v5 tests for DeveloperAccess.psm1
# 開発者用 IAM ポリシーの権限境界を固定する（#405）。
# - 変更系の操作を "*" に広げない（runner instance / Run Command ドキュメント / parameter prefix に限定）
# - default の IAM ユーザーに IAM の変更系を持たせない（アクセスキーを自分で作れないようにする）
# - IAM の読み取りは本プロジェクトのロールに限る。ワイルドカードのアクションは ec2:Describe* だけ（#380）
# - Admin ロールは指定した IAM ユーザーだけを、MFA 付きで信頼する
# - ARN へ埋め込む値に wildcard や広すぎる値を受け付けない
# 実際に IAM へ渡る形で検証するため、JSON へ変換して読み戻したものを対象にする。

BeforeDiscovery {
    # -ForEach のデータは discovery 時に評価されるため、BeforeAll ではなくここで定義する。
    $projectIamArns = @(
        'arn:aws:iam::123456789012:role/SquirrelNotifierAdmin',
        'arn:aws:iam::123456789012:role/SquirrelNotifierDesktopE2ERunner',
        'arn:aws:iam::123456789012:role/GitHubActionsSquirrelNotifierDesktopE2E',
        'arn:aws:iam::123456789012:instance-profile/SquirrelNotifierDesktopE2ERunnerProfile',
        'arn:aws:iam::123456789012:oidc-provider/token.actions.githubusercontent.com'
    )
}

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot 'DeveloperAccess.psm1') -Force

    $script:PolicyArgs = @{
        AccountId       = '123456789012'
        Region          = 'us-east-1'
        InstanceId      = 'i-0123456789abcdef0'
        ParameterPrefix     = '/squirrel-notifier/desktop-e2e'
        AdminRoleName       = 'SquirrelNotifierAdmin'
        RunnerRoleName      = 'SquirrelNotifierDesktopE2ERunner'
        InstanceProfileName = 'SquirrelNotifierDesktopE2ERunnerProfile'
        OidcRoleName        = 'GitHubActionsSquirrelNotifierDesktopE2E'
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
        @{ Action = 'iam:GetRolePolicy'; Expected = $projectIamArns }
        @{ Action = 'iam:SimulatePrincipalPolicy'; Expected = $projectIamArns }
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

    It 'IAM は読み取りと SimulatePrincipalPolicy だけを含む' {
        $iamActions = @($script:AllActions | Where-Object { $_ -like 'iam:*' })

        $iamActions | Should -Not -BeNullOrEmpty
        @($iamActions | Where-Object { $_ -notmatch '^iam:(Get|List)[A-Za-z]+$' -and $_ -ne 'iam:SimulatePrincipalPolicy' }) | Should -BeNullOrEmpty
    }

    It 'IAM の操作は本プロジェクトのロール・instance profile・OIDC provider だけを対象にする' -ForEach @(
        @{ Expected = $projectIamArns }
    ) {
        $iamStatements = @($script:Operator.Statement | Where-Object { @($_.Action) -like 'iam:*' })

        $iamStatements.Count | Should -Be 1
        (@($iamStatements[0].Resource) -join ',') | Should -Be ($Expected -join ',')
    }

    It 'ワイルドカードを含むアクションは ec2:Describe* だけ' {
        @($script:AllActions | Where-Object { $_.Contains('*') }) | Should -Be @('ec2:Describe*')
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

    It '<Name> に <Value> を渡すと ARN を広げずに拒否する' -ForEach @(
        @{ Name = 'ParameterPrefix'; Value = '/' }
        @{ Name = 'ParameterPrefix'; Value = '/*' }
        @{ Name = 'ParameterPrefix'; Value = '/squirrel-notifier' }
        @{ Name = 'ParameterPrefix'; Value = '/squirrel-notifier/*' }
        @{ Name = 'ParameterPrefix'; Value = 'squirrel-notifier/desktop-e2e' }
        @{ Name = 'AdminRoleName'; Value = '*' }
        @{ Name = 'AdminRoleName'; Value = 'path/SquirrelNotifierAdmin' }
        @{ Name = 'RunnerRoleName'; Value = '*' }
        @{ Name = 'InstanceProfileName'; Value = '*' }
        @{ Name = 'OidcRoleName'; Value = '*' }
        @{ Name = 'InstanceId'; Value = '*' }
        @{ Name = 'Region'; Value = '*' }
        @{ Name = 'AccountId'; Value = '*' }
    ) {
        $policyArgs = $script:PolicyArgs.Clone()
        $policyArgs[$Name] = $Value

        { New-DeveloperOperatorPolicy @policyArgs } | Should -Throw "*$Name*"
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

    It 'MFA を AssumeRole の条件にする（MFA 未登録では AdministratorAccess を引き受けられない）' {
        $condition = $script:Trust.Statement[0].Condition

        @($condition.PSObject.Properties.Name) | Should -Be @('Bool')
        $condition.Bool.'aws:MultiFactorAuthPresent' | Should -Be 'true'
    }

    It '<Name> に <Value> を渡すと拒否する' -ForEach @(
        @{ Name = 'UserName'; Value = '*' }
        @{ Name = 'UserName'; Value = 'path/developer' }
        @{ Name = 'AccountId'; Value = '*' }
    ) {
        $trustArgs = @{ AccountId = '123456789012'; UserName = 'developer' }
        $trustArgs[$Name] = $Value

        { New-DeveloperAdminTrustPolicy @trustArgs } | Should -Throw "*$Name*"
    }
}
