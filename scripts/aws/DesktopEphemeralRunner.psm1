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
      instance が無くなった使い捨て runner の登録を返す。
    .DESCRIPTION
      使い捨て runner の名前（Get-DesktopEphemeralRunnerName）を持ち、対応する instance が
      LiveInstanceIds に無く、job を実行中でないものを選ぶ。永続 runner など、名前の形式が
      違う runner は対象にしない。
    #>
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Runners,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$LiveInstanceIds
    )

    return @(
        foreach ($runner in $Runners)
        {
            $instanceId = Get-DesktopEphemeralInstanceIdFromRunnerName -RunnerName $runner.name
            if ($null -eq $instanceId -or $instanceId -in $LiveInstanceIds -or $runner.busy)
            {
                continue
            }

            $runner
        }
    )
}

Export-ModuleMember -Function @(
    'Get-DesktopEphemeralRunnerName',
    'Get-DesktopEphemeralInstanceIdFromRunnerName',
    'Select-ExpiredDesktopEphemeralInstance',
    'Select-OrphanedDesktopEphemeralRunner'
)
