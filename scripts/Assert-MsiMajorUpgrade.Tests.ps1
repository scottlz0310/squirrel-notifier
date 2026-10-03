BeforeAll {
    . (Join-Path $PSScriptRoot 'Assert-MsiMajorUpgrade.ps1')

    function New-UpgradeRow([string]$Maximum = '1.2.3', [int]$Attributes = 512, [string]$Action = 'WIX_UPGRADE_DETECTED') {
        ,@('upgrade-code', '', $Maximum, '', $Attributes.ToString(), '', $Action)
    }
}

Describe 'MSI の同一バージョン MajorUpgrade 判定' {
    It '同一バージョンを含む上限なら通す' -ForEach @(
        @{ Attributes = 512 }
        @{ Attributes = 768 }
    ) {
        $rows = @(New-UpgradeRow -Attributes $Attributes)
        { Assert-MsiMajorUpgrade -ProductVersion '1.2.3' -UpgradeRows $rows -MsiPath 'app.msi' } | Should -Not -Throw
    }

    It 'バージョンまたは inclusive 属性が違えば拒否する' -ForEach @(
        @{ Maximum = '1.2.2'; Attributes = 512 }
        @{ Maximum = '1.2.4'; Attributes = 512 }
        @{ Maximum = '1.2.3'; Attributes = 0 }
        @{ Maximum = '1.2.3'; Attributes = 256 }
    ) {
        $rows = @(New-UpgradeRow -Maximum $Maximum -Attributes $Attributes)
        { Assert-MsiMajorUpgrade -ProductVersion '1.2.3' -UpgradeRows $rows -MsiPath 'app.msi' } |
            Should -Throw '*同一バージョンの MajorUpgrade が有効ではありません*'
    }

    It 'ProductVersion の欠落を拒否する' -ForEach @(
        @{ Version = $null }
        @{ Version = '' }
        @{ Version = ' ' }
    ) {
        { Assert-MsiMajorUpgrade -ProductVersion $Version -UpgradeRows @() -MsiPath 'missing.msi' } |
            Should -Throw '*ProductVersion を取得できません: missing.msi*'
    }

    It '対象の Upgrade 行が無ければ拒否する' -ForEach @(
        @{ OtherRow = $false }
        @{ OtherRow = $true }
    ) {
        $rows = @(if ($OtherRow) { New-UpgradeRow -Action 'OTHER_ACTION' })
        { Assert-MsiMajorUpgrade -ProductVersion '1.2.3' -UpgradeRows $rows -MsiPath 'app.msi' } |
            Should -Throw '*WIX_UPGRADE_DETECTED の Upgrade table 行がありません*'
    }

    It '他の action の行を除外して対象行を選ぶ' {
        $rows = @((New-UpgradeRow -Maximum '9.9.9' -Action 'OTHER_ACTION'), (New-UpgradeRow))
        { Assert-MsiMajorUpgrade -ProductVersion '1.2.3' -UpgradeRows $rows -MsiPath 'app.msi' } | Should -Not -Throw
    }
}
