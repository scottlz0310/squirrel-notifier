# 使い捨て desktop E2E runner の名前付けと、TTL 回収の対象選定を行う（#380）。
# AWS / GitHub への書き込みは Invoke-DesktopE2EReaper.ps1 と workflow が行い、ここでは
# 入力と出力だけで判定する。Pester で契約を固定する。

Set-StrictMode -Version Latest

$script:RunnerNamePrefix = 'squirrel-notifier-ephemeral-'
$script:RunnerNamePattern = '^squirrel-notifier-ephemeral-(i-[0-9a-f]{8,17})$'
$script:InstanceIdPattern = '^i-[0-9a-f]{8,17}$'

# terminate の対象にする状態。shutting-down / terminated は既に破棄中か破棄済み。
$script:ReapableStates = @('pending', 'running', 'stopping', 'stopped')

function Get-DesktopEphemeralRunnerName
{
    <#
    .SYNOPSIS
      使い捨て instance に登録する JIT runner の名前を返す。
    .DESCRIPTION
      runner 名から instance ID を復元できるようにする。TTL 回収では、instance が無くなった runner
      登録をこの対応で見つける。既存の永続 runner（squirrel-notifier-desktop）とは重ならない。
    #>
    param(
        [Parameter(Mandatory)]
        [string]$InstanceId
    )

    if ($InstanceId -cnotmatch $script:InstanceIdPattern)
    {
        throw "InstanceId '$InstanceId' の形式が不正です。期待する形式: $script:InstanceIdPattern"
    }

    return "$script:RunnerNamePrefix$InstanceId"
}

function Get-DesktopEphemeralInstanceIdFromRunnerName
{
    <#
    .SYNOPSIS
      runner 名から instance ID を返す。使い捨て runner の名前でなければ $null を返す。
    #>
    param(
        [AllowEmptyString()]
        [string]$RunnerName
    )

    if ($RunnerName -cmatch $script:RunnerNamePattern)
    {
        return $Matches[1]
    }

    return $null
}

function ConvertTo-UtcDateTimeOffset
{
    <#
    .DESCRIPTION
      aws CLI の JSON を ConvertFrom-Json で読むと、PowerShell 7 は日時の文字列を DateTime へ変換する。
      文字列のまま来る場合もあるため、どちらも UTC の DateTimeOffset にそろえる。
    #>
    param($Value)

    if ($Value -is [datetimeoffset])
    {
        return $Value.ToUniversalTime()
    }

    if ($Value -is [datetime])
    {
        if ($Value.Kind -eq [System.DateTimeKind]::Unspecified)
        {
            throw "タイムゾーンの無い日時は比較できません: $Value"
        }

        return ([datetimeoffset]$Value).ToUniversalTime()
    }

    return [datetimeoffset]::Parse([string]$Value, [System.Globalization.CultureInfo]::InvariantCulture).ToUniversalTime()
}

function Select-ExpiredDesktopEphemeralInstance
{
    <#
    .SYNOPSIS
      起動から MaxAge を超えた使い捨て instance の ID を返す。
    .DESCRIPTION
      入力は describe-instances の Instances 要素（InstanceId / LaunchTime / State.Name）。
      呼び出し側で ephemeral-runner タグに絞った一覧を渡す。破棄中・破棄済みの状態は対象にしない。
    #>
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Instances,

        [Parameter(Mandatory)]
        [datetimeoffset]$Now,

        [Parameter(Mandatory)]
        [timespan]$MaxAge
    )

    $threshold = $Now.ToUniversalTime() - $MaxAge
    return @(
        foreach ($instance in $Instances)
        {
            if ($instance.State.Name -notin $script:ReapableStates)
            {
                continue
            }

            if ((ConvertTo-UtcDateTimeOffset -Value $instance.LaunchTime) -lt $threshold)
            {
                $instance.InstanceId
            }
        }
    )
}

function Select-OrphanedDesktopEphemeralRunner
{
    <#
    .SYNOPSIS
      instance が無くなった可能性のある使い捨て runner の登録（削除の候補）を返す。
    .DESCRIPTION
      使い捨て runner の名前（Get-DesktopEphemeralRunnerName）を持ち、対応する instance が
      KnownInstanceIds に無く、offline で job を実行中でないものを選ぶ。online の runner は
      どこかの instance から接続しているため候補にしない。

      instance 一覧は取得した時点のものなので、その後に起動・登録された runner も候補に入り得る。
      呼び出し側は削除の直前に instance ID ごとに状態を確かめ直すこと。
    #>
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Runners,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$KnownInstanceIds
    )

    return @(
        foreach ($runner in $Runners)
        {
            $instanceId = Get-DesktopEphemeralInstanceIdFromRunnerName -RunnerName $runner.name
            if ($null -eq $instanceId -or $instanceId -in $KnownInstanceIds -or $runner.busy -or $runner.status -ne 'offline')
            {
                continue
            }

            $runner
        }
    )
}

function Select-InconsistentDesktopEphemeralRunner
{
    <#
    .SYNOPSIS
      online なのに対応する instance が一覧に無い使い捨て runner を返す。
    .DESCRIPTION
      online の runner は instance から接続しているため、instance 一覧に無いのは一覧の取得が
      誤っていることを示す（region の設定違い、空応答など）。呼び出し側はこれが 1 件でもあれば、
      何も書き込まずに止まること。
    #>
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Runners,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$KnownInstanceIds
    )

    return @(
        foreach ($runner in $Runners)
        {
            $instanceId = Get-DesktopEphemeralInstanceIdFromRunnerName -RunnerName $runner.name
            if ($null -ne $instanceId -and $runner.status -eq 'online' -and $instanceId -notin $KnownInstanceIds)
            {
                $runner
            }
        }
    )
}

function Test-DesktopEphemeralInstanceGone
{
    <#
    .SYNOPSIS
      instance の状態から、instance が無くなったことを確認できるかを返す。
    .DESCRIPTION
      shutting-down と terminated だけを「無くなった」とみなす。

      $null や空文字（InvalidInstanceID.NotFound や空応答）は false とする。NotFound は破棄から
      時間がたった instance だけでなく、作成直後でまだ Describe に反映されていない instance や
      region の設定違いでも返るため、見分けられない。削除の判断には使わず、runner を残す
      （使われなかった ephemeral runner の登録は GitHub が 1 日で自動削除する）。
    #>
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]$State
    )

    return $State -in @('shutting-down', 'terminated')
}

Export-ModuleMember -Function @(
    'Get-DesktopEphemeralRunnerName',
    'Get-DesktopEphemeralInstanceIdFromRunnerName',
    'Select-ExpiredDesktopEphemeralInstance',
    'Select-OrphanedDesktopEphemeralRunner',
    'Select-InconsistentDesktopEphemeralRunner',
    'Test-DesktopEphemeralInstanceGone'
)
