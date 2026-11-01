function Assert-MsiUpgradeFilePolicy {
    param(
        [Parameter(Mandatory)][object[]]$CustomActions,
        [Parameter(Mandatory)][hashtable]$Sequences
    )

    $action = @($CustomActions | Where-Object { $_[0] -eq 'SetREINSTALLMODE' })
    if ($action.Count -ne 1 -or [int]$action[0][1] -ne 51 -or
        $action[0][2] -cne 'REINSTALLMODE' -or $action[0][3] -cne 'amus') {
        throw 'MSI に major upgrade 用の REINSTALLMODE=amus 設定がありません。'
    }

    foreach ($name in @('InstallUISequence', 'InstallExecuteSequence')) {
        $rows = @($Sequences[$name])
        $find = $rows | Where-Object { $_[0] -eq 'FindRelatedProducts' } | Select-Object -First 1
        $set = $rows | Where-Object { $_[0] -eq 'SetREINSTALLMODE' } | Select-Object -First 1
        $cost = $rows | Where-Object { $_[0] -eq 'CostInitialize' } | Select-Object -First 1
        if ($null -eq $find -or $null -eq $set -or $null -eq $cost -or
            [int]$find[2] -ge [int]$set[2] -or [int]$set[2] -ge [int]$cost[2] -or
            $set[1] -cne 'WIX_UPGRADE_DETECTED AND NOT Installed') {
            throw "MSI の $name で major upgrade のファイル判定を Costing 前に設定できません。"
        }
    }

    $register = $Sequences['InstallExecuteSequence'] |
        Where-Object { $_[0] -eq 'RegisterScheduledTask' } | Select-Object -First 1
    if ($null -eq $register -or $register[1] -cne 'NOT Installed AND NOT WIX_UPGRADE_DETECTED') {
        throw 'MSI が major upgrade 時に既存タスクを上書きする設定です。'
    }
}
