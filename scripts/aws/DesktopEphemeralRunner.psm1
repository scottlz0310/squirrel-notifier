# 使い捨て desktop E2E runner の名前付け、起動・後片付けの要求の組み立て、TTL 回収の対象選定、
# OIDC ロールの権限境界の確認を行う（#380）。
# AWS / GitHub への書き込みは scripts/aws の Start / Stop / Test / Invoke スクリプトが行い、ここでは
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

function Get-DesktopEphemeralRunnerLabel
{
    <#
    .SYNOPSIS
      使い捨て runner だけに付ける run 固有のラベルを返す。
    .DESCRIPTION
      永続 runner のラベル（squirrel-notifier-desktop）は付けない。付けると、別の run の job や
      永続 runner 向けの job をこの runner が拾い得る。
    #>
    param(
        [Parameter(Mandatory)]
        [string]$RunId
    )

    if ($RunId -cnotmatch '^[1-9][0-9]{0,19}$')
    {
        throw "RunId '$RunId' の形式が不正です。GitHub Actions の run ID（数字）を指定してください。"
    }

    return "squirrel-notifier-desktop-$RunId"
}

function New-DesktopEphemeralJitConfigRequest
{
    <#
    .SYNOPSIS
      generate-jitconfig API の要求本文を返す。
    #>
    param(
        [Parameter(Mandatory)]
        [string]$InstanceId,

        [Parameter(Mandatory)]
        [string]$RunId
    )

    return [ordered]@{
        name            = Get-DesktopEphemeralRunnerName -InstanceId $InstanceId
        # 1 は repository の既定の runner group（Default）。
        runner_group_id = 1
        labels          = @('self-hosted', 'windows', (Get-DesktopEphemeralRunnerLabel -RunId $RunId))
        work_folder     = '_work'
    }
}

function New-DesktopEphemeralJitParameterRequest
{
    <#
    .SYNOPSIS
      JIT config を置く ssm put-parameter の --cli-input-json に渡す値を返す。
    .DESCRIPTION
      JIT config は 4 KB を超えるため Advanced tier を使う（Standard の上限は 4,096 バイト）。
      Advanced tier でだけ使える有効期限ポリシーを付け、cleanup と reaper の両方が漏れても
      AWS が削除するようにする。値を --value で渡すとコマンドラインに載るため、ファイル経由で渡す。
      Overwrite は false にし、同じ instance の parameter を上書きしない。
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$Value,

        [Parameter(Mandatory)]
        [datetimeoffset]$ExpiresAt
    )

    $timestamp = $ExpiresAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
    $policies = ConvertTo-Json -Compress -Depth 4 -InputObject @(
        [ordered]@{ Type = 'Expiration'; Version = '1.0'; Attributes = [ordered]@{ Timestamp = $timestamp } }
    )

    return [ordered]@{
        Name      = $Name
        Value     = $Value
        Type      = 'SecureString'
        Tier      = 'Advanced'
        Overwrite = $false
        Policies  = $policies
    }
}

function Get-DesktopEphemeralRunnerState
{
    <#
    .SYNOPSIS
      runner 一覧から、指定した使い捨て runner の状態を返す。
    .DESCRIPTION
      missing / offline / online / busy / invalid-label のいずれかを返す。invalid-label は同じ名前の
      runner に run 固有のラベルが無いことを示し、待っても解消しないため呼び出し側は失敗させる。
    #>
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Runners,

        [Parameter(Mandatory)]
        [string]$RunnerName,

        [Parameter(Mandatory)]
        [string]$Label
    )

    $runner = @($Runners | Where-Object { $_.name -ceq $RunnerName }) | Select-Object -First 1
    if ($null -eq $runner)
    {
        return 'missing'
    }

    if ($Label -notin @($runner.labels | ForEach-Object { $_.name }))
    {
        return 'invalid-label'
    }

    if ($runner.status -ne 'online')
    {
        return 'offline'
    }

    if ($runner.busy)
    {
        return 'busy'
    }

    return 'online'
}

function Get-DesktopE2EOidcBoundaryCase
{
    <#
    .SYNOPSIS
      OIDC ロールの権限境界を DryRun で確かめる操作の一覧を返す（#380）。
    .DESCRIPTION
      RunInstances は Launch Template の default version のままなら許可され、network interface
      （subnet / Security Group）・block device・instance type を要求で上書きすると拒否されること、
      ephemeral-runner タグの無い instance（永続 instance）は terminate できないことを確かめる。

      上書きの値には永続 instance の subnet と Security Group を使う。subnet は Launch Template と
      同じ値でも、要求で指定した時点で Launch Template 由来のリソースではなくなるため拒否される想定。
    #>
    param(
        [Parameter(Mandatory)]
        [ValidatePattern('^lt-[0-9a-f]{8,17}$')]
        [string]$LaunchTemplateId,

        [Parameter(Mandatory)]
        [ValidatePattern('^i-[0-9a-f]{8,17}$')]
        [string]$ReferenceInstanceId,

        [Parameter(Mandatory)]
        [ValidatePattern('^subnet-[0-9a-f]{8,17}$')]
        [string]$ReferenceSubnetId,

        [Parameter(Mandatory)]
        [ValidatePattern('^sg-[0-9a-f]{8,17}$')]
        [string]$ReferenceSecurityGroupId
    )

    $launch = @('ec2', 'run-instances', '--dry-run', '--count', '1', '--launch-template', "LaunchTemplateId=$LaunchTemplateId,Version=`$Default")

    return @(
        [pscustomobject]@{ Name = 'launch-template-default'; Expected = 'allowed'; Arguments = $launch }
        [pscustomobject]@{
            Name      = 'override-network-interface'
            Expected  = 'denied'
            Arguments = $launch + @('--network-interfaces', "DeviceIndex=0,SubnetId=$ReferenceSubnetId,Groups=$ReferenceSecurityGroupId")
        }
        # root volume（/dev/sda1）の変更は ec2:IsLaunchTemplateResource では拒否されない（2026-09-23 の実測）。
        # OIDC ロールの volume の条件（AMI の root volume の容量・種類が上限）で拒否されることを確かめる。
        [pscustomobject]@{
            Name      = 'override-block-device'
            Expected  = 'denied'
            Arguments = $launch + @('--block-device-mappings', 'DeviceName=/dev/sda1,Ebs={VolumeSize=128}')
        }
        [pscustomobject]@{
            Name      = 'override-volume-type'
            Expected  = 'denied'
            Arguments = $launch + @('--block-device-mappings', 'DeviceName=/dev/sda1,Ebs={VolumeType=io2,Iops=3000}')
        }
        [pscustomobject]@{
            Name      = 'add-extra-volume'
            Expected  = 'denied'
            Arguments = $launch + @('--block-device-mappings', 'DeviceName=/dev/sdf,Ebs={VolumeSize=8,VolumeType=gp3}')
        }
        [pscustomobject]@{ Name = 'override-instance-type'; Expected = 'denied'; Arguments = $launch + @('--instance-type', 't3.micro') }
        [pscustomobject]@{
            Name      = 'terminate-persistent-instance'
            Expected  = 'denied'
            Arguments = @('ec2', 'terminate-instances', '--dry-run', '--instance-ids', $ReferenceInstanceId)
        }
    )
}

function Resolve-DesktopE2EDryRunOutcome
{
    <#
    .SYNOPSIS
      aws CLI の --dry-run の結果を allowed / denied / unknown に分類する。
    .DESCRIPTION
      DryRunOperation は認可された、UnauthorizedOperation は IAM で拒否されたことを示す。
      それ以外（入力の誤り、存在しないリソース、通信の失敗など）は認可の判定になっていないため
      unknown とし、呼び出し側は境界を確かめられなかったとして失敗させる。
    #>
    param(
        [Parameter(Mandatory)]
        [int]$ExitCode,

        [AllowEmptyString()]
        [string]$Output
    )

    if ($ExitCode -ne 0 -and $Output.Contains('(DryRunOperation)'))
    {
        return 'allowed'
    }

    if ($ExitCode -ne 0 -and $Output.Contains('(UnauthorizedOperation)'))
    {
        return 'denied'
    }

    return 'unknown'
}

Export-ModuleMember -Function @(
    'Get-DesktopEphemeralRunnerName',
    'Get-DesktopEphemeralInstanceIdFromRunnerName',
    'Select-ExpiredDesktopEphemeralInstance',
    'Select-OrphanedDesktopEphemeralRunner',
    'Select-InconsistentDesktopEphemeralRunner',
    'Test-DesktopEphemeralInstanceGone',
    'Get-DesktopEphemeralRunnerLabel',
    'New-DesktopEphemeralJitConfigRequest',
    'New-DesktopEphemeralJitParameterRequest',
    'Get-DesktopEphemeralRunnerState',
    'Get-DesktopE2EOidcBoundaryCase',
    'Resolve-DesktopE2EDryRunOutcome'
)
