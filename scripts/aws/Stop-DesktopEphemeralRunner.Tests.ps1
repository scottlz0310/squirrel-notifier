# Pester v5 tests for Stop-DesktopEphemeralRunner.ps1
# 使い捨て runner の後片付けを固定する（#380）。
# - ephemeral-runner タグのある instance だけを terminate し、タグが無ければ何も変更せずに失敗する
# - DeleteOnTermination=false の volume（要求で指定でき IAM で拒否できない）は terminate 後に切り離しを待って削除する
# - 破棄中・破棄済み・存在しない instance は terminate しない
# - JIT parameter と runner 登録が無いことは成功とし、それ以外の失敗は握り潰さない
# - job を実行中の runner 登録は削除しない
# aws / gh CLI は、呼び出しを記録して状態に応じた応答を返す関数で置き換える。

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'Stop-DesktopEphemeralRunner.ps1'
    $script:InstanceId = 'i-0aaaaaaaaaaaaaaaa'

    function global:aws
    {
        $operation = "$($args[0]) $($args[1])"
        $global:FakeCalls.Add(($args -join ' '))
        $global:LASTEXITCODE = 0
        $fake = $global:FakeState

        switch ($operation)
        {
            'ec2 describe-instances'
            {
                if ($null -eq $fake.Instance)
                {
                    $global:LASTEXITCODE = 254
                    return 'An error occurred (InvalidInstanceID.NotFound) when calling the DescribeInstances operation'
                }
                return ($fake.Instance | ConvertTo-Json -Compress)
            }
            'ec2 terminate-instances' { return '{}' }
            'ec2 wait' { return '' }
            'ec2 delete-volume' { return '' }
            'ssm delete-parameter'
            {
                if ($fake.ParameterError)
                {
                    $global:LASTEXITCODE = 254
                    return "An error occurred ($($fake.ParameterError)) when calling the DeleteParameter operation"
                }
                return ''
            }
            default { throw "想定外の aws 呼び出し: $($args -join ' ')" }
        }
    }

    function global:gh
    {
        $global:FakeCalls.Add('gh ' + ($args -join ' '))
        $global:LASTEXITCODE = 0
        if ($args -contains 'DELETE')
        {
            return ''
        }

        return @($global:FakeState.Runners | ForEach-Object { $_ | ConvertTo-Json -Compress })
    }

    function Invoke-Stop
    {
        & $script:ScriptPath -Repository 'scottlz0310/squirrel-notifier' -InstanceId $script:InstanceId | ConvertFrom-Json
    }

    function Get-WriteCalls
    {
        return @($global:FakeCalls | Where-Object { $_ -match '^(ec2 terminate-instances|ec2 delete-volume|ssm delete-parameter|gh api -X DELETE)' })
    }
}

AfterAll {
    Remove-Item -Path 'function:global:aws', 'function:global:gh' -ErrorAction SilentlyContinue
    Remove-Variable -Name 'FakeCalls', 'FakeState' -Scope Global -ErrorAction SilentlyContinue
}

Describe 'Stop-DesktopEphemeralRunner.ps1' {
    BeforeEach {
        $global:FakeCalls = [System.Collections.Generic.List[string]]::new()
        $global:FakeState = @{
            Instance       = [ordered]@{ State = 'running'; Tag = 'ephemeral-runner'; RetainedVolumes = @() }
            ParameterError = $null
            Runners        = @(
                [ordered]@{ id = 1; name = 'squirrel-notifier-desktop'; status = 'online'; busy = $false }
                [ordered]@{ id = 7; name = 'squirrel-notifier-ephemeral-i-0aaaaaaaaaaaaaaaa'; status = 'offline'; busy = $false }
            )
        }
    }

    It '使い捨て instance を terminate し、parameter と runner 登録を削除する' {
        $result = Invoke-Stop

        $result.instance | Should -Be 'terminated'
        $result.parameter | Should -Be 'deleted'
        $result.runner | Should -Be 'deleted'
        Get-WriteCalls | Should -Be @(
            "ec2 terminate-instances --region us-east-1 --instance-ids $script:InstanceId"
            "ssm delete-parameter --region us-east-1 --name /squirrel-notifier/desktop-e2e/jit/$script:InstanceId"
            'gh api -X DELETE repos/scottlz0310/squirrel-notifier/actions/runners/7'
        )
    }

    It 'DeleteOnTermination=false の volume は terminate 後に切り離しを待って削除する' {
        $global:FakeState.Instance.RetainedVolumes = @('vol-0aaaaaaaaaaaaaaaa')

        $result = Invoke-Stop

        @($result.deletedVolumes) | Should -Be @('vol-0aaaaaaaaaaaaaaaa')
        $calls = @($global:FakeCalls)
        $terminate = [array]::FindIndex($calls, [Predicate[string]] { param($c) $c -like 'ec2 terminate-instances*' })
        $wait = [array]::FindIndex($calls, [Predicate[string]] { param($c) $c -like 'ec2 wait volume-available*vol-0aaaaaaaaaaaaaaaa*' })
        $delete = [array]::FindIndex($calls, [Predicate[string]] { param($c) $c -eq 'ec2 delete-volume --region us-east-1 --volume-id vol-0aaaaaaaaaaaaaaaa' })
        $terminate | Should -BeGreaterThan -1
        $wait | Should -BeGreaterThan $terminate
        $delete | Should -BeGreaterThan $wait
    }

    It '残す volume が無ければ volume を待たず、削除もしない' {
        (Invoke-Stop).deletedVolumes | Should -BeNullOrEmpty
        @($global:FakeCalls | Where-Object { $_ -like 'ec2 wait*' -or $_ -like 'ec2 delete-volume*' }) | Should -BeNullOrEmpty
    }

    It 'ephemeral-runner タグが無い instance は何も変更せずに失敗する' {
        $global:FakeState.Instance = [ordered]@{ State = 'stopped'; Tag = $null; RetainedVolumes = @('vol-0bbbbbbbbbbbbbbbb') }

        { Invoke-Stop } | Should -Throw '*terminate しません*'
        Get-WriteCalls | Should -BeNullOrEmpty
    }

    It '<State> の instance は terminate しない' -ForEach @(
        @{ State = 'shutting-down' }
        @{ State = 'terminated' }
    ) {
        $global:FakeState.Instance = [ordered]@{ State = $State; Tag = 'ephemeral-runner'; RetainedVolumes = @() }

        (Invoke-Stop).instance | Should -Be $State
        @($global:FakeCalls | Where-Object { $_ -like 'ec2 terminate-instances*' }) | Should -BeNullOrEmpty
    }

    It '存在しない instance・parameter・runner は成功として扱う' {
        $global:FakeState.Instance = $null
        $global:FakeState.ParameterError = 'ParameterNotFound'
        $global:FakeState.Runners = @($global:FakeState.Runners[0])

        $result = Invoke-Stop

        $result.instance | Should -Be 'not-found'
        $result.parameter | Should -Be 'not-found'
        $result.runner | Should -Be 'not-found'
    }

    It 'job を実行中の runner 登録は削除しない' {
        $global:FakeState.Runners[1].busy = $true

        (Invoke-Stop).runner | Should -Be 'kept-busy'
        @($global:FakeCalls | Where-Object { $_ -like 'gh api -X DELETE*' }) | Should -BeNullOrEmpty
    }

    It 'parameter の削除で NotFound 以外の失敗は握り潰さない' {
        $global:FakeState.ParameterError = 'AccessDeniedException'

        { Invoke-Stop } | Should -Throw '*AccessDeniedException*'
    }
}
