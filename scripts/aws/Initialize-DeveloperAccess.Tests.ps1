# Pester v5 tests for Initialize-DeveloperAccess.ps1
# 書き込みの順序と失敗時の安全性を固定する（#405）。
# - 入力値が不正なら、何も作らずに止まる
# - Admin ロールの作成が失敗したら、ユーザーへの権限付与（inline policy / SignIn policy）を行わない
# - ユーザーへの権限付与は Admin ロールの作成・更新より後に行う
# aws CLI は、呼び出しを記録して指定した操作だけ失敗させる関数で置き換える。

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'Initialize-DeveloperAccess.ps1'

    # 関数はコマンド解決で外部実行ファイルより優先されるため、スクリプト内の `& aws` はこちらを呼ぶ。
    function global:aws
    {
        $operation = "$($args[0]) $($args[1])"
        $global:FakeAwsCalls.Add($operation)

        if ($operation -eq $global:FakeAwsFailOn)
        {
            $global:LASTEXITCODE = 1
            return 'simulated failure'
        }

        switch ($operation)
        {
            'sts get-caller-identity' { $global:LASTEXITCODE = 0; return '123456789012' }
            'iam get-user' { $global:LASTEXITCODE = 0; return 'developer' }
            'iam get-role' { $global:LASTEXITCODE = 254; return 'NoSuchEntity' }
            default { $global:LASTEXITCODE = 0; return '' }
        }
    }

    function Invoke-InitializeScript
    {
        param([hashtable]$Overrides = @{})

        $arguments = @{ UserName = 'developer'; InstanceId = 'i-0123456789abcdef0' }
        foreach ($key in $Overrides.Keys)
        {
            $arguments[$key] = $Overrides[$key]
        }

        & $script:ScriptPath @arguments | Out-Null
    }

    $script:WriteOperations = @(
        'iam create-user',
        'iam create-role',
        'iam update-assume-role-policy',
        'iam attach-role-policy',
        'iam put-user-policy',
        'iam attach-user-policy'
    )
}

AfterAll {
    Remove-Item -Path 'function:global:aws' -ErrorAction SilentlyContinue
    Remove-Variable -Name 'FakeAwsCalls', 'FakeAwsFailOn' -Scope Global -ErrorAction SilentlyContinue
}

Describe 'Initialize-DeveloperAccess.ps1' {
    BeforeEach {
        $global:FakeAwsCalls = [System.Collections.Generic.List[string]]::new()
        $global:FakeAwsFailOn = $null
    }

    It '<Name> が不正なら何も書き込まずに止まる' -ForEach @(
        @{ Name = 'ParameterPrefix'; Value = '/' }
        @{ Name = 'AdminRoleName'; Value = '*' }
        @{ Name = 'InstanceId'; Value = '*' }
        @{ Name = 'UserName'; Value = '*' }
    ) {
        { Invoke-InitializeScript -Overrides @{ $Name = $Value } } | Should -Throw "*$Name*"

        @($global:FakeAwsCalls | Where-Object { $_ -in $script:WriteOperations }) | Should -BeNullOrEmpty
    }

    It 'Admin ロールの作成に失敗したら、ユーザーへ権限を付与しない' {
        $global:FakeAwsFailOn = 'iam create-role'

        { Invoke-InitializeScript } | Should -Throw '*create-role*'

        $global:FakeAwsCalls | Should -Not -Contain 'iam put-user-policy'
        $global:FakeAwsCalls | Should -Not -Contain 'iam attach-user-policy'
    }

    It 'ユーザーへの権限付与は Admin ロールの作成と AdministratorAccess の付与より後に行う' {
        Invoke-InitializeScript

        $calls = @($global:FakeAwsCalls)
        $grantIndexes = @($calls.IndexOf('iam put-user-policy'), $calls.IndexOf('iam attach-user-policy'))

        $grantIndexes | Should -Not -Contain -1
        $calls.IndexOf('iam create-role') | Should -BeLessThan ($grantIndexes | Measure-Object -Minimum).Minimum
        $calls.IndexOf('iam attach-role-policy') | Should -BeLessThan ($grantIndexes | Measure-Object -Minimum).Minimum
    }
}
