# Pester v5 tests for Invoke-DesktopE2EReaper.ps1
# 回収の範囲と失敗時の扱いを固定する（#380）。
# - terminate するのは期限切れの使い捨て instance だけ
# - JIT parameter は terminate した instance と破棄済みの instance の分だけ削除し、NotFound は無視する
# - runner 登録は、instance が無くなった使い捨て runner だけを削除する
# - NotFound 以外の失敗は握り潰さない。-WhatIf では何も書き込まない
# aws / gh CLI は、呼び出しを記録して状態に応じた応答を返す関数で置き換える。

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'Invoke-DesktopE2EReaper.ps1'

    # 関数はコマンド解決で外部実行ファイルより優先されるため、スクリプト内の `& aws` / `& gh` はこちらを呼ぶ。
    function global:aws
    {
        $operation = "$($args[0]) $($args[1])"
        $global:FakeCalls.Add(($args -join ' '))
        $global:LASTEXITCODE = 0
        $fake = $global:FakeState

        switch ($operation)
        {
            'ec2 describe-instances' { return (ConvertTo-Json -InputObject @($fake.Instances) -Depth 5) }
            'ec2 terminate-instances' { return '{}' }
            'ssm delete-parameter'
            {
                $name = $args[[array]::IndexOf($args, '--name') + 1]
                if ($name -in $fake.MissingParameters)
                {
                    $global:LASTEXITCODE = 254
                    return 'An error occurred (ParameterNotFound) when calling the DeleteParameter operation'
                }
                if ($fake.DeleteParameterError)
                {
                    $global:LASTEXITCODE = 254
                    return "An error occurred ($($fake.DeleteParameterError)) when calling the DeleteParameter operation"
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

    function New-Instance
    {
        param(
            [string]$Id,
            [int]$AgeMinutes,
            [string]$State = 'running'
        )

        return [ordered]@{ InstanceId = $Id; LaunchTime = [datetimeoffset]::UtcNow.AddMinutes(-$AgeMinutes).ToString('o'); State = [ordered]@{ Name = $State } }
    }

    function Invoke-Reaper
    {
        param([switch]$WhatIf)

        & $script:ScriptPath -Repository 'scottlz0310/squirrel-notifier' -WhatIf:$WhatIf | ConvertFrom-Json
    }

    function Get-WriteCalls
    {
        return @($global:FakeCalls | Where-Object { $_ -match '^(ec2 terminate-instances|ssm delete-parameter|gh api -X DELETE)' })
    }
}

AfterAll {
    Remove-Item -Path 'function:global:aws', 'function:global:gh' -ErrorAction SilentlyContinue
    Remove-Variable -Name 'FakeCalls', 'FakeState' -Scope Global -ErrorAction SilentlyContinue
}

Describe 'Invoke-DesktopE2EReaper.ps1' {
    BeforeEach {
        $global:FakeCalls = [System.Collections.Generic.List[string]]::new()
        $global:FakeState = @{
            Instances            = @(
                (New-Instance -Id 'i-0aaaaaaaaaaaaaaaa' -AgeMinutes 30)
                (New-Instance -Id 'i-0bbbbbbbbbbbbbbbb' -AgeMinutes 180)
                (New-Instance -Id 'i-0cccccccccccccccc' -AgeMinutes 300 -State 'terminated')
            )
            MissingParameters    = @()
            DeleteParameterError = $null
            Runners              = @(
                [ordered]@{ id = 1; name = 'squirrel-notifier-desktop'; status = 'offline'; busy = $false }
                [ordered]@{ id = 2; name = 'squirrel-notifier-ephemeral-i-0aaaaaaaaaaaaaaaa'; status = 'online'; busy = $false }
                [ordered]@{ id = 3; name = 'squirrel-notifier-ephemeral-i-0bbbbbbbbbbbbbbbb'; status = 'online'; busy = $false }
                [ordered]@{ id = 4; name = 'squirrel-notifier-ephemeral-i-0cccccccccccccccc'; status = 'offline'; busy = $false }
            )
        }
    }

    It '期限切れの使い捨て instance だけを terminate する' {
        $result = Invoke-Reaper

        @($global:FakeCalls | Where-Object { $_ -like 'ec2 terminate-instances*' }) | Should -Be @('ec2 terminate-instances --region us-east-1 --instance-ids i-0bbbbbbbbbbbbbbbb')
        @($result.terminatedInstances) | Should -Be @('i-0bbbbbbbbbbbbbbbb')
        @($result.liveInstances) | Should -Be @('i-0aaaaaaaaaaaaaaaa')
    }

    It 'terminate した instance と破棄済みの instance の JIT parameter を削除する' {
        $result = Invoke-Reaper

        @($result.deletedParameters) | Should -Be @('/squirrel-notifier/desktop-e2e/jit/i-0bbbbbbbbbbbbbbbb', '/squirrel-notifier/desktop-e2e/jit/i-0cccccccccccccccc')
        @($global:FakeCalls | Where-Object { $_ -like '*jit/i-0aaaaaaaaaaaaaaaa*' }) | Should -BeNullOrEmpty
    }

    It 'JIT parameter が既に無い場合は成功として扱う' {
        $global:FakeState.MissingParameters = @('/squirrel-notifier/desktop-e2e/jit/i-0cccccccccccccccc')

        $result = Invoke-Reaper

        @($result.deletedParameters) | Should -Be @('/squirrel-notifier/desktop-e2e/jit/i-0bbbbbbbbbbbbbbbb')
    }

    It 'parameter の削除が NotFound 以外で失敗したら止まる' {
        $global:FakeState.DeleteParameterError = 'AccessDeniedException'

        { Invoke-Reaper } | Should -Throw '*AccessDeniedException*'
    }

    It 'instance が無くなった使い捨て runner だけを削除する' {
        $result = Invoke-Reaper

        @($global:FakeCalls | Where-Object { $_ -like 'gh api -X DELETE*' }) | Should -Be @(
            'gh api -X DELETE repos/scottlz0310/squirrel-notifier/actions/runners/3',
            'gh api -X DELETE repos/scottlz0310/squirrel-notifier/actions/runners/4'
        )
        @($result.deletedRunners) | Should -Not -Contain 'squirrel-notifier-desktop'
    }

    It '使い捨て instance が 1 台も無くても失敗しない' {
        $global:FakeState.Instances = @()
        $global:FakeState.Runners = @()

        $result = Invoke-Reaper

        @($result.terminatedInstances) | Should -BeNullOrEmpty
        Get-WriteCalls | Should -BeNullOrEmpty
    }

    It '-WhatIf では何も書き込まない' {
        Invoke-Reaper -WhatIf | Out-Null

        Get-WriteCalls | Should -BeNullOrEmpty
    }
}
