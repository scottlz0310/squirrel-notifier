# Pester v5 tests for Invoke-DesktopE2EReaper.ps1
# 回収の範囲と失敗時の扱いを固定する（#380）。
# - terminate するのは期限切れの使い捨て instance だけ
# - JIT parameter は terminate した instance と破棄済みの instance の分だけ削除し、NotFound は無視する
# - runner 登録は、削除の直前に取り直した instance が無くなっている offline の使い捨て runner だけを削除する
#   （一覧の取得後に起動・登録された runner は残す）
# - online の使い捨て runner に対応する instance が一覧に無ければ（region 違い・空応答）、何も書き込まずに止まる
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
            'ec2 describe-instances'
            {
                # 一覧の取得（--filters）と、削除直前の instance ID ごとの取り直し（--instance-ids）を分ける。
                if ($args -contains '--instance-ids')
                {
                    $instanceId = $args[[array]::IndexOf($args, '--instance-ids') + 1]
                    if ($fake.CurrentStates.ContainsKey($instanceId))
                    {
                        return $fake.CurrentStates[$instanceId]
                    }

                    $global:LASTEXITCODE = 254
                    return "An error occurred (InvalidInstanceID.NotFound) when calling the DescribeInstances operation: The instance ID '$instanceId' does not exist"
                }

                return (ConvertTo-Json -InputObject @($fake.Instances) -Depth 5)
            }
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

    function New-Runner
    {
        param(
            [int]$Id,
            [string]$Name,
            [string]$Status = 'offline',
            [bool]$Busy = $false
        )

        return [ordered]@{ id = $Id; name = $Name; status = $Status; busy = $Busy }
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

    function Get-DeletedRunnerCalls
    {
        return @($global:FakeCalls | Where-Object { $_ -like 'gh api -X DELETE*' })
    }
}

AfterAll {
    Remove-Item -Path 'function:global:aws', 'function:global:gh' -ErrorAction SilentlyContinue
    Remove-Variable -Name 'FakeCalls', 'FakeState' -Scope Global -ErrorAction SilentlyContinue
}

Describe 'Invoke-DesktopE2EReaper.ps1' {
    BeforeEach {
        $global:FakeCalls = [System.Collections.Generic.List[string]]::new()
        # a: 稼働中（30 分）/ b: 期限切れ（180 分）/ c: 破棄済み
        $global:FakeState = @{
            Instances            = @(
                (New-Instance -Id 'i-0aaaaaaaaaaaaaaaa' -AgeMinutes 30)
                (New-Instance -Id 'i-0bbbbbbbbbbbbbbbb' -AgeMinutes 180)
                (New-Instance -Id 'i-0cccccccccccccccc' -AgeMinutes 300 -State 'terminated')
            )
            # 削除直前の取り直しで返す状態。無い instance ID は InvalidInstanceID.NotFound になる。
            CurrentStates        = @{
                'i-0aaaaaaaaaaaaaaaa' = 'running'
                'i-0bbbbbbbbbbbbbbbb' = 'shutting-down'
            }
            MissingParameters    = @()
            DeleteParameterError = $null
            Runners              = @(
                (New-Runner -Id 1 -Name 'squirrel-notifier-desktop')
                (New-Runner -Id 2 -Name 'squirrel-notifier-ephemeral-i-0aaaaaaaaaaaaaaaa' -Status 'online')
                (New-Runner -Id 3 -Name 'squirrel-notifier-ephemeral-i-0bbbbbbbbbbbbbbbb')
                (New-Runner -Id 4 -Name 'squirrel-notifier-ephemeral-i-0cccccccccccccccc')
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

    It '取り直した instance が無くなっている offline の使い捨て runner だけを削除する' {
        $result = Invoke-Reaper

        Get-DeletedRunnerCalls | Should -Be @(
            'gh api -X DELETE repos/scottlz0310/squirrel-notifier/actions/runners/3',
            'gh api -X DELETE repos/scottlz0310/squirrel-notifier/actions/runners/4'
        )
        @($result.deletedRunners) | Should -Not -Contain 'squirrel-notifier-desktop'
    }

    It '一覧の取得後に起動・登録された runner は、取り直した instance が <State> なら残す' -ForEach @(
        @{ State = 'pending' }
        @{ State = 'running' }
    ) {
        # reaper の一覧取得と、E2E の RunInstances / JIT 登録が並行した状況
        $global:FakeState.Runners += (New-Runner -Id 5 -Name 'squirrel-notifier-ephemeral-i-0dddddddddddddddd')
        $global:FakeState.CurrentStates['i-0dddddddddddddddd'] = $State

        $result = Invoke-Reaper

        Get-DeletedRunnerCalls | Should -Not -Contain 'gh api -X DELETE repos/scottlz0310/squirrel-notifier/actions/runners/5'
        @($result.keptRunners) | Should -Be @('squirrel-notifier-ephemeral-i-0dddddddddddddddd')
    }

    It 'instance が 0 台で online の使い捨て runner があれば、何も書き込まずに止まる（region 違い・空応答）' {
        $global:FakeState.Instances = @()
        $global:FakeState.Runners = @(New-Runner -Id 6 -Name 'squirrel-notifier-ephemeral-i-0aaaaaaaaaaaaaaaa' -Status 'online')

        { Invoke-Reaper } | Should -Throw '*online*i-0aaaaaaaaaaaaaaaa*'

        Get-WriteCalls | Should -BeNullOrEmpty
    }

    It '期限切れの instance があっても、不整合を見つけたら terminate しない' {
        $global:FakeState.Runners += (New-Runner -Id 7 -Name 'squirrel-notifier-ephemeral-i-0eeeeeeeeeeeeeeee' -Status 'online')

        { Invoke-Reaper } | Should -Throw '*i-0eeeeeeeeeeeeeeee*'

        Get-WriteCalls | Should -BeNullOrEmpty
    }

    It 'instance が 0 台で offline の使い捨て runner は、取り直しても無ければ削除する' {
        $global:FakeState.Instances = @()
        $global:FakeState.CurrentStates = @{}
        $global:FakeState.Runners = @(New-Runner -Id 8 -Name 'squirrel-notifier-ephemeral-i-0aaaaaaaaaaaaaaaa')

        Invoke-Reaper | Out-Null

        Get-DeletedRunnerCalls | Should -Be @('gh api -X DELETE repos/scottlz0310/squirrel-notifier/actions/runners/8')
    }

    It '使い捨て instance も runner も無ければ何もしない' {
        $global:FakeState.Instances = @()
        $global:FakeState.Runners = @()

        $result = Invoke-Reaper

        @($result.terminatedInstances) | Should -BeNullOrEmpty
        Get-WriteCalls | Should -BeNullOrEmpty
    }

    It '-WhatIf では何も書き込まない' {
        Invoke-Reaper -WhatIf 6>$null | Out-Null

        Get-WriteCalls | Should -BeNullOrEmpty
    }
}
