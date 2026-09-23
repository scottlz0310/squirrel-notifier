# Pester v5 tests for DesktopEphemeralRunner.psm1
# 使い捨て runner の名前付けと TTL 回収の対象選定を固定する（#380）。
# - runner 名と instance ID は相互に変換でき、永続 runner の名前とは重ならない
# - 起動から MaxAge を超え、まだ破棄されていない instance だけを回収の対象にする
# - instance が無くなった offline の使い捨て runner だけを削除の候補にし、job 実行中・online・永続 runner は除く
# - online なのに instance が一覧に無い使い捨て runner は、一覧の誤り（region 違い・空応答）として検出する

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot 'DesktopEphemeralRunner.psm1') -Force

    $script:Now = [datetimeoffset]::Parse('2026-09-23T12:00:00+00:00')

    function New-Instance
    {
        param(
            [string]$Id,
            $LaunchTime,
            [string]$State = 'running'
        )

        return [pscustomobject]@{ InstanceId = $Id; LaunchTime = $LaunchTime; State = [pscustomobject]@{ Name = $State } }
    }
}

Describe 'Get-DesktopEphemeralRunnerName / Get-DesktopEphemeralInstanceIdFromRunnerName' {
    It 'runner 名から instance ID を復元できる' {
        $name = Get-DesktopEphemeralRunnerName -InstanceId 'i-04f5c608ccd18fb92'

        $name | Should -Be 'squirrel-notifier-ephemeral-i-04f5c608ccd18fb92'
        Get-DesktopEphemeralInstanceIdFromRunnerName -RunnerName $name | Should -Be 'i-04f5c608ccd18fb92'
    }

    It '<Name> は使い捨て runner の名前として扱わない' -ForEach @(
        @{ Name = 'squirrel-notifier-desktop' }
        @{ Name = 'ami-verify-i-04f5c608ccd18fb92' }
        @{ Name = 'squirrel-notifier-ephemeral-i-*' }
        @{ Name = 'squirrel-notifier-ephemeral-i-04f5c608ccd18fb92-extra' }
        @{ Name = 'SQUIRREL-NOTIFIER-EPHEMERAL-i-04f5c608ccd18fb92' }
        @{ Name = '' }
    ) {
        Get-DesktopEphemeralInstanceIdFromRunnerName -RunnerName $Name | Should -BeNullOrEmpty
    }

    It '不正な instance ID からは名前を作らない' {
        { Get-DesktopEphemeralRunnerName -InstanceId 'i-*' } | Should -Throw '*InstanceId*'
    }
}

Describe 'Select-ExpiredDesktopEphemeralInstance' {
    It '<Case> は回収の対象が <Expected>' -ForEach @(
        @{ Case = '起動から 121 分（running）'; LaunchTime = '2026-09-23T09:59:00+00:00'; State = 'running'; Expected = $true }
        @{ Case = '起動から 119 分（running）'; LaunchTime = '2026-09-23T10:01:00+00:00'; State = 'running'; Expected = $false }
        @{ Case = '起動から 121 分（stopped）'; LaunchTime = '2026-09-23T09:59:00+00:00'; State = 'stopped'; Expected = $true }
        @{ Case = '起動から 121 分（pending）'; LaunchTime = '2026-09-23T09:59:00+00:00'; State = 'pending'; Expected = $true }
        @{ Case = '起動から 121 分（shutting-down）'; LaunchTime = '2026-09-23T09:59:00+00:00'; State = 'shutting-down'; Expected = $false }
        @{ Case = '起動から 121 分（terminated）'; LaunchTime = '2026-09-23T09:59:00+00:00'; State = 'terminated'; Expected = $false }
        @{ Case = '別のタイムゾーン表記で起動から 121 分'; LaunchTime = '2026-09-23T18:59:00+09:00'; State = 'running'; Expected = $true }
    ) {
        $instances = @(New-Instance -Id 'i-0123456789abcdef0' -LaunchTime $LaunchTime -State $State)

        $selected = @(Select-ExpiredDesktopEphemeralInstance -Instances $instances -Now $script:Now -MaxAge ([timespan]::FromMinutes(120)))

        ($selected -contains 'i-0123456789abcdef0') | Should -Be $Expected
    }

    It 'ConvertFrom-Json が日時を DateTime に変換した場合も比較できる' {
        $instances = @('[{"InstanceId":"i-0123456789abcdef0","LaunchTime":"2026-09-23T09:59:00+00:00","State":{"Name":"running"}}]' | ConvertFrom-Json)

        @(Select-ExpiredDesktopEphemeralInstance -Instances $instances -Now $script:Now -MaxAge ([timespan]::FromMinutes(120))) | Should -Be @('i-0123456789abcdef0')
    }

    It 'タイムゾーンの無い日時は推測せずに拒否する' {
        $instances = @(New-Instance -Id 'i-0123456789abcdef0' -LaunchTime ([datetime]::new(2026, 9, 23, 9, 0, 0, [System.DateTimeKind]::Unspecified)))

        { Select-ExpiredDesktopEphemeralInstance -Instances $instances -Now $script:Now -MaxAge ([timespan]::FromMinutes(120)) } | Should -Throw '*タイムゾーン*'
    }

    It '空の一覧では何も返さない' {
        @(Select-ExpiredDesktopEphemeralInstance -Instances @() -Now $script:Now -MaxAge ([timespan]::FromMinutes(120))) | Should -BeNullOrEmpty
    }
}

Describe 'Select-OrphanedDesktopEphemeralRunner / Select-InconsistentDesktopEphemeralRunner' {
    BeforeAll {
        $script:Runners = @(
            [pscustomobject]@{ id = 1; name = 'squirrel-notifier-desktop'; status = 'offline'; busy = $false }
            [pscustomobject]@{ id = 2; name = 'squirrel-notifier-ephemeral-i-0aaaaaaaaaaaaaaaa'; status = 'online'; busy = $false }
            [pscustomobject]@{ id = 3; name = 'squirrel-notifier-ephemeral-i-0bbbbbbbbbbbbbbbb'; status = 'offline'; busy = $false }
            [pscustomobject]@{ id = 4; name = 'squirrel-notifier-ephemeral-i-0cccccccccccccccc'; status = 'online'; busy = $true }
            [pscustomobject]@{ id = 5; name = 'squirrel-notifier-ephemeral-i-0dddddddddddddddd'; status = 'online'; busy = $false }
        )
        $script:Known = @('i-0aaaaaaaaaaaaaaaa', 'i-0cccccccccccccccc')
        $script:Orphaned = @(Select-OrphanedDesktopEphemeralRunner -Runners $script:Runners -KnownInstanceIds $script:Known)
        $script:Inconsistent = @(Select-InconsistentDesktopEphemeralRunner -Runners $script:Runners -KnownInstanceIds $script:Known)
    }

    It '一覧に instance が無い offline の使い捨て runner だけを削除の候補にする' {
        @($script:Orphaned.id) | Should -Be @(3)
    }

    It '<Case> は削除の候補にしない' -ForEach @(
        @{ Case = '永続 runner'; Id = 1 }
        @{ Case = 'instance が一覧にある runner'; Id = 2 }
        @{ Case = 'job を実行中の runner'; Id = 4 }
        @{ Case = 'instance が一覧に無くても online の runner'; Id = 5 }
    ) {
        @($script:Orphaned.id) | Should -Not -Contain $Id
    }

    It 'online なのに instance が一覧に無い使い捨て runner を不整合として返す' {
        @($script:Inconsistent.id) | Should -Be @(5)
    }

    It 'instance 一覧が空で online の使い捨て runner があれば不整合になる（region の設定違い・空応答）' {
        $runners = @([pscustomobject]@{ id = 7; name = 'squirrel-notifier-ephemeral-i-0aaaaaaaaaaaaaaaa'; status = 'online'; busy = $false })

        @(Select-InconsistentDesktopEphemeralRunner -Runners $runners -KnownInstanceIds @()).id | Should -Be @(7)
    }
}

Describe 'Test-DesktopEphemeralInstanceGone' {
    It '取り直した状態が <State> なら <Expected>' -ForEach @(
        @{ State = $null; Expected = $true }
        @{ State = ''; Expected = $true }
        @{ State = 'shutting-down'; Expected = $true }
        @{ State = 'terminated'; Expected = $true }
        @{ State = 'pending'; Expected = $false }
        @{ State = 'running'; Expected = $false }
        @{ State = 'stopping'; Expected = $false }
        @{ State = 'stopped'; Expected = $false }
    ) {
        Test-DesktopEphemeralInstanceGone -State $State | Should -Be $Expected
    }
}
