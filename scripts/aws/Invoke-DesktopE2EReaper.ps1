<#
.SYNOPSIS
  workflow の cleanup から漏れた使い捨て desktop E2E runner を回収する（#380）。
.DESCRIPTION
  desktop-e2e-reaper.yml から定期実行する。workflow の cleanup（if: always()）が本来の後始末で、
  本スクリプトは runner 障害や cleanup 自体の失敗に備えた保険である。

  1. ephemeral-runner タグの instance のうち、起動から -MaxAgeMinutes を超えたものを terminate する
  2. terminate した instance と、破棄中・破棄済みの instance の JIT config parameter を削除する
     （parameter には有効期限ポリシーも付けるため、ここでの削除は二重の保険）
  3. 対応する instance が無くなった使い捨て runner の登録を削除する。候補は offline で job を実行して
     いないものだけで、削除の直前に instance ID ごとに状態を取り直し、破棄中・破棄済みを確認できた
     ときだけ削除する（一覧の取得後に起動・登録された runner を消さないため）。取り直しが
     InvalidInstanceID.NotFound や空応答のときは、作成直後の未反映・region の設定違いと見分けられない
     ため残す。使われなかった ephemeral runner の登録は GitHub が 1 日で自動削除する

  読み取り（instance 一覧と runner 一覧）はすべて書き込みより前に行う。online の使い捨て runner に
  対応する instance が一覧に無ければ、一覧の取得が誤っている（region の設定違い、空応答など）として
  何も書き込まずに止まる。

  instance を terminate できるのは OIDC ロールの条件（ephemeral-runner タグ）を満たすものだけで、
  既存の永続 instance は対象にならない。runner 名の形式が違う永続 runner も削除しない。

  aws CLI（OIDC ロールの資格情報）と gh CLI（GH_TOKEN に Administration: write の App token）を使う。
.EXAMPLE
  .\Invoke-DesktopE2EReaper.ps1 -Repository scottlz0310/squirrel-notifier -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository,

    [ValidateNotNullOrEmpty()]
    [string]$Region = 'us-east-1',

    # desktop E2E の最長経路（準備 15 分 + E2E 45 分 + cleanup 10 分）と job の待ち時間より長くする。
    [ValidateRange(90, 1440)]
    [int]$MaxAgeMinutes = 120,

    [ValidatePattern('^(/[A-Za-z0-9_.-]+){2,}$')]
    [string]$JitParameterPrefix = '/squirrel-notifier/desktop-e2e/jit'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'DesktopEphemeralRunner.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'DesktopRunnerImage.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'DesktopE2ECli.psm1') -Force

$tag = Get-DesktopRunnerResourceTag
$now = [datetimeoffset]::UtcNow

# 空配列 [] を ConvertFrom-Json すると $null になり、@() で包むと要素 1 個の配列になるため除外する。
$instances = @(
    Invoke-AwsCli -Arguments @(
        'ec2', 'describe-instances',
        '--region', $Region,
        '--filters', "Name=tag:$($tag.Key),Values=$($tag.EphemeralInstanceValue)",
        '--query', 'Reservations[].Instances[].{InstanceId: InstanceId, LaunchTime: LaunchTime, State: State}',
        '--output', 'json'
    ) | ConvertFrom-Json | Where-Object { $null -ne $_ }
)

$expired = @(Select-ExpiredDesktopEphemeralInstance -Instances $instances -Now $now -MaxAge ([timespan]::FromMinutes($MaxAgeMinutes)))
$gone = @($instances | Where-Object { Test-DesktopEphemeralInstanceGone -State $_.State.Name } | ForEach-Object { $_.InstanceId })
$existing = @($instances | Where-Object { -not (Test-DesktopEphemeralInstanceGone -State $_.State.Name) } | ForEach-Object { $_.InstanceId })
$live = @($existing | Where-Object { $_ -notin $expired })

$runners = @(
    Invoke-GhApi -Arguments @("repos/$Repository/actions/runners?per_page=100", '--paginate', '--jq', '.runners[] | {id, name, status, busy}') |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { $_ | ConvertFrom-Json }
)

# online の使い捨て runner は instance から接続している。その instance が一覧に無いなら、
# 一覧の取得が誤っている（region の設定違い、空応答など）。回収の判断を誤らないよう、何も書き込まずに止まる。
$inconsistent = @(Select-InconsistentDesktopEphemeralRunner -Runners $runners -KnownInstanceIds $existing)
if ($inconsistent.Count -gt 0)
{
    throw "online の使い捨て runner に対応する instance が region $Region の一覧にありません: $($inconsistent.name -join ', ')。region の設定と instance の状態を確認してください。何も変更していません。"
}

$terminated = @()
if ($expired.Count -gt 0 -and $PSCmdlet.ShouldProcess(($expired -join ', '), '期限切れの使い捨て instance を terminate'))
{
    Invoke-AwsCli -Arguments (@('ec2', 'terminate-instances', '--region', $Region, '--instance-ids') + $expired) | Out-Null
    $terminated = $expired
}

$deletedParameters = @()
foreach ($instanceId in @($terminated + $gone | Select-Object -Unique))
{
    $name = "$JitParameterPrefix/$instanceId"
    if ($PSCmdlet.ShouldProcess($name, 'JIT config の parameter を削除'))
    {
        $result = Invoke-AwsCli -Arguments @('ssm', 'delete-parameter', '--region', $Region, '--name', $name) -AbsentErrorCode 'ParameterNotFound'
        if ($null -ne $result)
        {
            $deletedParameters += $name
        }
    }
}

$candidates = @(Select-OrphanedDesktopEphemeralRunner -Runners $runners -KnownInstanceIds $live)

$deletedRunners = @()
$keptRunners = @()
foreach ($runner in $candidates)
{
    # 一覧の取得後に起動・登録された runner を消さないよう、削除の直前に instance を取り直す。
    # 破棄中・破棄済みを確認できたときだけ削除し、NotFound や空応答では残す（fail-closed）。
    $instanceId = Get-DesktopEphemeralInstanceIdFromRunnerName -RunnerName $runner.name
    $state = Invoke-AwsCli -Arguments @(
        'ec2', 'describe-instances',
        '--region', $Region,
        '--instance-ids', $instanceId,
        '--query', 'Reservations[0].Instances[0].State.Name',
        '--output', 'text'
    ) -AbsentErrorCode 'InvalidInstanceID.NotFound'

    if (-not (Test-DesktopEphemeralInstanceGone -State $state))
    {
        $observed = if ([string]::IsNullOrEmpty($state)) { 'NotFound または空応答（判断できない）' } else { $state }
        Write-Verbose "runner $($runner.name) の instance は $observed のため残します。"
        $keptRunners += $runner.name
        continue
    }

    if ($PSCmdlet.ShouldProcess("$($runner.name) (id $($runner.id))", 'runner 登録を削除'))
    {
        Invoke-GhApi -Arguments @('-X', 'DELETE', "repos/$Repository/actions/runners/$($runner.id)") | Out-Null
        $deletedRunners += $runner.name
    }
}

[pscustomobject]@{
    schemaVersion       = 1
    checkedAt           = $now.ToString('o')
    maxAgeMinutes       = $MaxAgeMinutes
    liveInstances       = $live
    terminatedInstances = $terminated
    deletedParameters   = $deletedParameters
    deletedRunners      = $deletedRunners
    keptRunners         = $keptRunners
} | ConvertTo-Json -Depth 4
