# Pester v5 tests for DesktopEphemeralRunner.psm1
# 使い捨て runner の名前付けと TTL 回収の対象選定を固定する（#380）。
# - runner 名と instance ID は相互に変換でき、永続 runner の名前とは重ならない
# - 起動から MaxAge を超え、まだ破棄されていない instance だけを回収の対象にする
# - instance が無くなった offline の使い捨て runner だけを削除の候補にし、job 実行中・online・永続 runner は除く
# - online なのに instance が一覧に無い使い捨て runner は、一覧の誤り（region 違い・空応答）として検出する
# - JIT runner のラベルは run 固有のものだけにし、JIT parameter は Advanced tier・有効期限つき・上書きなしで置く
# - runner の状態を missing / offline / online / busy / invalid-label に分類する
# - OIDC ロールの境界確認は default version の起動だけを許可、上書きと永続 instance の terminate を拒否と想定し、
#   DryRun の結果は DryRunOperation / UnauthorizedOperation 以外をすべて判定不能とする

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
        @{ State = $null; Expected = $false }
        @{ State = ''; Expected = $false }
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

Describe 'Get-DesktopEphemeralRunnerLabel' {
    It 'run ID から run 固有のラベルを作る' {
        Get-DesktopEphemeralRunnerLabel -RunId '35844074479' | Should -Be 'squirrel-notifier-desktop-35844074479'
    }

    It 'run ID が <RunId> なら拒否する' -ForEach @(
        @{ RunId = '' }
        @{ RunId = '0' }
        @{ RunId = '12a' }
        @{ RunId = '1,squirrel-notifier-desktop' }
    ) {
        { Get-DesktopEphemeralRunnerLabel -RunId $RunId } | Should -Throw
    }
}

Describe 'New-DesktopEphemeralJitConfigRequest' {
    BeforeAll {
        $script:Request = New-DesktopEphemeralJitConfigRequest -InstanceId 'i-04f5c608ccd18fb92' -RunId '123'
    }

    It 'runner 名は instance ID から復元できる形にする' {
        $script:Request.name | Should -Be 'squirrel-notifier-ephemeral-i-04f5c608ccd18fb92'
    }

    It 'ラベルは run 固有のものだけで、永続 runner のラベルを含めない' {
        $script:Request.labels | Should -Be @('self-hosted', 'windows', 'squirrel-notifier-desktop-123')
        $script:Request.labels | Should -Not -Contain 'squirrel-notifier-desktop'
    }

    It '既定の runner group に登録する' {
        $script:Request.runner_group_id | Should -Be 1
    }
}

Describe 'New-DesktopEphemeralJitParameterRequest' {
    BeforeAll {
        $script:Parameter = New-DesktopEphemeralJitParameterRequest -Name '/squirrel-notifier/desktop-e2e/jit/i-04f5c608ccd18fb92' -Value 'secret' `
            -ExpiresAt ([datetimeoffset]::Parse('2026-09-23T14:00:00+09:00'))
    }

    It 'Advanced tier の SecureString にし、上書きしない' {
        $script:Parameter.Type | Should -Be 'SecureString'
        $script:Parameter.Tier | Should -Be 'Advanced'
        $script:Parameter.Overwrite | Should -BeFalse
    }

    It '有効期限ポリシーの時刻を UTC にそろえる' {
        $policies = @($script:Parameter.Policies | ConvertFrom-Json)

        $policies.Count | Should -Be 1
        $policies[0].Type | Should -Be 'Expiration'
        $policies[0].Version | Should -Be '1.0'
        # ConvertFrom-Json は日時の文字列を DateTime に変換するため、AWS に渡す文字列そのものを確かめる。
        $script:Parameter.Policies | Should -BeLike '*"Timestamp":"2026-09-23T05:00:00.000Z"*'
    }
}

Describe 'Get-DesktopEphemeralRunnerState' {
    BeforeAll {
        function New-LabeledRunner
        {
            param(
                [string]$Name,
                [string]$Status,
                [bool]$Busy = $false,
                [string[]]$Labels = @('self-hosted', 'windows', 'squirrel-notifier-desktop-123')
            )

            return [pscustomobject]@{ name = $Name; status = $Status; busy = $Busy; labels = @($Labels | ForEach-Object { [pscustomobject]@{ name = $_ } }) }
        }

        $script:Name = 'squirrel-notifier-ephemeral-i-04f5c608ccd18fb92'
        $script:Label = 'squirrel-notifier-desktop-123'
    }

    It '<Case> は <Expected>' -ForEach @(
        @{ Case = '登録前'; Runner = $null; Expected = 'missing' }
        @{ Case = '登録済みで未接続'; Runner = @{ Status = 'offline' }; Expected = 'offline' }
        @{ Case = '接続済みで待機中'; Runner = @{ Status = 'online' }; Expected = 'online' }
        @{ Case = '接続済みで job 実行中'; Runner = @{ Status = 'online'; Busy = $true }; Expected = 'busy' }
        @{ Case = 'run 固有のラベルが無い'; Runner = @{ Status = 'online'; Labels = @('self-hosted', 'windows', 'squirrel-notifier-desktop') }; Expected = 'invalid-label' }
    ) {
        $runners = @(
            (New-LabeledRunner -Name 'squirrel-notifier-desktop' -Status 'online' -Labels @('squirrel-notifier-desktop'))
            if ($null -ne $Runner) { New-LabeledRunner -Name $script:Name @Runner }
        )

        Get-DesktopEphemeralRunnerState -Runners $runners -RunnerName $script:Name -Label $script:Label | Should -Be $Expected
    }

    It '名前は大文字小文字を区別して比べる' {
        $runners = @(New-LabeledRunner -Name $script:Name.ToUpperInvariant() -Status 'online')

        Get-DesktopEphemeralRunnerState -Runners $runners -RunnerName $script:Name -Label $script:Label | Should -Be 'missing'
    }
}

Describe 'Get-DesktopE2EOidcBoundaryCase' {
    BeforeAll {
        $script:Cases = Get-DesktopE2EOidcBoundaryCase -LaunchTemplateId 'lt-09e208553b742f6c1' -ReferenceInstanceId 'i-00b4e23b910eade6c' `
            -ReferenceSubnetId 'subnet-05a6dbdf30e0b6664' -ReferenceSecurityGroupId 'sg-0f5751845b3fa696b'
    }

    It 'Launch Template の default version だけが許可される想定になっている' {
        @($script:Cases | Where-Object Expected -EQ 'allowed').Name | Should -Be @('launch-template-default')
    }

    It 'すべての操作が --dry-run 付きである' {
        foreach ($case in $script:Cases)
        {
            $case.Arguments | Should -Contain '--dry-run'
        }
    }

    It '<Name> は拒否される想定で、<Option> を上書きする' -ForEach @(
        @{ Name = 'override-network-interface'; Option = '--network-interfaces' }
        @{ Name = 'override-block-device'; Option = '--block-device-mappings' }
        @{ Name = 'override-instance-type'; Option = '--instance-type' }
    ) {
        $case = $script:Cases | Where-Object Name -EQ $Name

        $case.Expected | Should -Be 'denied'
        $case.Arguments | Should -Contain $Option
        $case.Arguments | Should -Contain 'LaunchTemplateId=lt-09e208553b742f6c1,Version=$Default'
    }

    It '永続 instance の terminate は拒否される想定になっている' {
        $case = $script:Cases | Where-Object Name -EQ 'terminate-persistent-instance'

        $case.Expected | Should -Be 'denied'
        $case.Arguments | Should -Be @('ec2', 'terminate-instances', '--dry-run', '--instance-ids', 'i-00b4e23b910eade6c')
    }

    It 'network interface の上書きには永続 instance の subnet と Security Group を使う' {
        ($script:Cases | Where-Object Name -EQ 'override-network-interface').Arguments |
            Should -Contain 'DeviceIndex=0,SubnetId=subnet-05a6dbdf30e0b6664,Groups=sg-0f5751845b3fa696b'
    }
}

Describe 'Resolve-DesktopE2EDryRunOutcome' {
    It '<Case> は <Expected>' -ForEach @(
        @{ Case = 'DryRunOperation'; ExitCode = 254; Output = 'An error occurred (DryRunOperation) when calling the RunInstances operation: Request would have succeeded, but DryRun flag is set.'; Expected = 'allowed' }
        @{ Case = 'UnauthorizedOperation'; ExitCode = 254; Output = 'An error occurred (UnauthorizedOperation) when calling the RunInstances operation: You are not authorized to perform this operation.'; Expected = 'denied' }
        @{ Case = '入力の誤り'; ExitCode = 254; Output = 'An error occurred (InvalidParameterCombination) when calling the RunInstances operation'; Expected = 'unknown' }
        @{ Case = '資格情報の失効'; ExitCode = 255; Output = 'An error occurred (ExpiredToken) when calling the RunInstances operation'; Expected = 'unknown' }
        @{ Case = '--dry-run が効かず成功した'; ExitCode = 0; Output = '{"Instances": []}'; Expected = 'unknown' }
        @{ Case = '終了コード 0 で DryRunOperation の文字列を含む'; ExitCode = 0; Output = '(DryRunOperation)'; Expected = 'unknown' }
    ) {
        Resolve-DesktopE2EDryRunOutcome -ExitCode $ExitCode -Output $Output | Should -Be $Expected
    }
}
