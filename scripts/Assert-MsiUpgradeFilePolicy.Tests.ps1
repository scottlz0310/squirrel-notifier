BeforeAll {
    . (Join-Path $PSScriptRoot 'Assert-MsiUpgradeFilePolicy.ps1')
}

Describe 'MSI の更新時ファイル選択とタスク保持' {
    BeforeEach {
        $actions = @(,@('SetREINSTALLMODE', '51', 'REINSTALLMODE', 'amus'))
        $sequences = @{}
        foreach ($name in @('InstallUISequence', 'InstallExecuteSequence')) {
            $sequences[$name] = @(
                ,@('FindRelatedProducts', '', '25')
                ,@('SetREINSTALLMODE', 'WIX_UPGRADE_DETECTED AND NOT Installed', '799')
                ,@('CostInitialize', '', '800')
                ,@('RegisterScheduledTask', 'NOT Installed AND NOT WIX_UPGRADE_DETECTED', '4001')
            )
        }
    }

    It 'GUI と silent の両方で Costing 前に更新時だけ再配置する設定を通す' {
        { Assert-MsiUpgradeFilePolicy -CustomActions $actions -Sequences $sequences } | Should -Not -Throw
    }

    It '強制配置の欠落・異なる値・異なる action を拒否する: <Case>' -ForEach @(
        @{ Case = 'missing' }
        @{ Case = 'value' }
        @{ Case = 'type' }
        @{ Case = 'property' }
    ) {
        switch ($Case) {
            missing { $actions[0][0] = 'OtherAction' }
            value { $actions[0][3] = 'omus' }
            type { $actions[0][1] = '1' }
            property { $actions[0][2] = 'OTHER' }
        }
        { Assert-MsiUpgradeFilePolicy -CustomActions $actions -Sequences $sequences } | Should -Throw '*REINSTALLMODE=amus*'
    }

    It '各 sequence の設定順序・条件の退行を拒否する: <Sequence>/<Case>' -ForEach @(
        @{ Sequence = 'InstallUISequence'; Case = 'late' }
        @{ Sequence = 'InstallExecuteSequence'; Case = 'late' }
        @{ Sequence = 'InstallUISequence'; Case = 'early' }
        @{ Sequence = 'InstallExecuteSequence'; Case = 'early' }
        @{ Sequence = 'InstallUISequence'; Case = 'unconditional' }
        @{ Sequence = 'InstallExecuteSequence'; Case = 'unconditional' }
        @{ Sequence = 'InstallExecuteSequence'; Case = 'missing' }
    ) {
        switch ($Case) {
            late { $sequences[$Sequence][1][2] = '1000' }
            early { $sequences[$Sequence][1][2] = '24' }
            unconditional { $sequences[$Sequence][1][1] = '1' }
            missing { $sequences[$Sequence] = @() }
        }
        { Assert-MsiUpgradeFilePolicy -CustomActions $actions -Sequences $sequences } | Should -Throw '*Costing 前*'
    }

    It '更新時のタスク上書きを拒否する' {
        $sequences.InstallExecuteSequence[3][1] = 'NOT Installed'
        { Assert-MsiUpgradeFilePolicy -CustomActions $actions -Sequences $sequences } | Should -Throw '*タスクを上書き*'
    }
}
