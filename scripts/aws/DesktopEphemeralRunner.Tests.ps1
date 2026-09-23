# Pester v5 tests for DesktopEphemeralRunner.psm1
# 使い捨て runner の名前付けと TTL 回収の対象選定を固定する（#380）。
# - runner 名と instance ID は相互に変換でき、永続 runner の名前とは重ならない
# - 起動から MaxAge を超え、まだ破棄されていない instance だけを回収の対象にする
# - instance が無くなった使い捨て runner だけを削除の対象にし、job 実行中と永続 runner は除く

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

Describe 'Select-OrphanedDesktopEphemeralRunner' {
    BeforeAll {
        $script:Runners = @(
            [pscustomobject]@{ id = 1; name = 'squirrel-notifier-desktop'; status = 'offline'; busy = $false }
            [pscustomobject]@{ id = 2; name = 'squirrel-notifier-ephemeral-i-0aaaaaaaaaaaaaaaa'; status = 'online'; busy = $false }
            [pscustomobject]@{ id = 3; name = 'squirrel-notifier-ephemeral-i-0bbbbbbbbbbbbbbbb'; status = 'offline'; busy = $false }
            [pscustomobject]@{ id = 4; name = 'squirrel-notifier-ephemeral-i-0cccccccccccccccc'; status = 'online'; busy = $true }
        )
        $script:Selected = @(Select-OrphanedDesktopEphemeralRunner -Runners $script:Runners -LiveInstanceIds @('i-0aaaaaaaaaaaaaaaa'))
    }

    It 'instance が無くなった使い捨て runner を選ぶ' {
        @($script:Selected.id) | Should -Contain 3
    }

    It '<Case> は選ばない' -ForEach @(
        @{ Case = '永続 runner'; Id = 1 }
        @{ Case = 'instance が生きている runner'; Id = 2 }
        @{ Case = 'job を実行中の runner'; Id = 4 }
    ) {
        @($script:Selected.id) | Should -Not -Contain $Id
    }
}
