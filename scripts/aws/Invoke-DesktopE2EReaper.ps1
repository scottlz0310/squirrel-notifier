<#
.SYNOPSIS
  workflow の cleanup から漏れた使い捨て desktop E2E runner を回収する（#380）。
.DESCRIPTION
  desktop-e2e-reaper.yml から定期実行する。workflow の cleanup（if: always()）が本来の後始末で、
  本スクリプトは runner 障害や cleanup 自体の失敗に備えた保険である。

  1. ephemeral-runner タグの instance のうち、起動から -MaxAgeMinutes を超えたものを terminate する
  2. terminate した instance と、破棄中・破棄済みの instance の JIT config parameter を削除する
     （parameter には有効期限ポリシーも付けるため、ここでの削除は二重の保険）
  3. 対応する instance が無くなった使い捨て runner の登録を削除する（job 実行中のものは除く）

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

function Invoke-AwsCli
{
    <#
    .SYNOPSIS
      aws CLI を呼び出し、失敗を例外へ変換する。
    .DESCRIPTION
      -AbsentErrorCode は「存在しない」を成功とみなしたい場合だけに使い、その AWS エラーコードの
      失敗に限って $null を返す。権限不足まで握り潰すと、回収できていないことに気付けない。
    #>
    param(
        [string[]]$Arguments,
        [string]$AbsentErrorCode
    )

    $output = & aws @Arguments 2>&1
    if ($LASTEXITCODE -ne 0)
    {
        if ($AbsentErrorCode -and ($output | Out-String).Contains("($AbsentErrorCode)"))
        {
            return $null
        }

        throw "aws CLI が失敗しました: aws $($Arguments -join ' ')`n$output"
    }

    return ($output | Out-String).Trim()
}

function Invoke-GhApi
{
    param([string[]]$Arguments)

    $output = & gh api @Arguments 2>&1
    if ($LASTEXITCODE -ne 0)
    {
        throw "gh api が失敗しました: gh api $($Arguments -join ' ')`n$output"
    }

    return $output
}

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
$gone = @($instances | Where-Object { $_.State.Name -in @('shutting-down', 'terminated') } | ForEach-Object { $_.InstanceId })
$live = @($instances | Where-Object { $_.State.Name -notin @('shutting-down', 'terminated') -and $_.InstanceId -notin $expired } | ForEach-Object { $_.InstanceId })

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

$runners = @(
    Invoke-GhApi -Arguments @("repos/$Repository/actions/runners?per_page=100", '--paginate', '--jq', '.runners[] | {id, name, status, busy}') |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { $_ | ConvertFrom-Json }
)
$orphaned = @(Select-OrphanedDesktopEphemeralRunner -Runners $runners -LiveInstanceIds $live)

$deletedRunners = @()
foreach ($runner in $orphaned)
{
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
} | ConvertTo-Json -Depth 4
