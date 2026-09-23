<#
.SYNOPSIS
  使い捨て desktop E2E instance を terminate し、JIT config parameter と runner 登録を削除する（#380）。
.DESCRIPTION
  desktop-e2e.yml の cleanup-runner（runner_mode: ephemeral、if: always()）から呼ぶ。

  1. instance に ephemeral-runner タグがあることを確かめてから terminate する。タグが無い instance は
     永続 instance の可能性があるため、何もせずに失敗させる（OIDC ロールの条件でも拒否される）
  2. JIT config parameter を削除する（有効期限ポリシーもあるため二重の保険）
  3. runner 登録が残っていれば削除する。ephemeral runner は job を 1 つ実行すると自分で登録を消すが、
     job が始まらなかった場合（準備の失敗・cancel）は残る。job を実行中の登録は削除しない

  instance・parameter・runner がすでに無いことは成功として扱う。それ以外の失敗は握り潰さない。
  漏れた場合は desktop-e2e-reaper.yml が回収する。

  aws CLI（OIDC ロールの資格情報）と gh CLI（GH_TOKEN に Administration: write の App token）を使う。
.EXAMPLE
  .\Stop-DesktopEphemeralRunner.ps1 -Repository scottlz0310/squirrel-notifier -InstanceId i-0123456789abcdef0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository,

    [Parameter(Mandatory)]
    [ValidatePattern('^i-[0-9a-f]{8,17}$')]
    [string]$InstanceId,

    [ValidateNotNullOrEmpty()]
    [string]$Region = 'us-east-1',

    [ValidatePattern('^(/[A-Za-z0-9_.-]+){2,}$')]
    [string]$JitParameterPrefix = '/squirrel-notifier/desktop-e2e/jit'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'DesktopEphemeralRunner.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'DesktopRunnerImage.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'DesktopE2ECli.psm1') -Force

$tag = Get-DesktopRunnerResourceTag

$described = Invoke-AwsCli -Arguments @(
    'ec2', 'describe-instances',
    '--region', $Region,
    '--instance-ids', $InstanceId,
    '--query', "Reservations[0].Instances[0].{State: State.Name, Tag: Tags[?Key=='$($tag.Key)'].Value | [0]}",
    '--output', 'json'
) -AbsentErrorCode 'InvalidInstanceID.NotFound'

$instance = if ($null -eq $described) { $null } else { $described | ConvertFrom-Json }
$instanceResult = if ($null -eq $instance)
{
    'not-found'
}
elseif ($instance.Tag -cne $tag.EphemeralInstanceValue)
{
    throw "instance $InstanceId に $($tag.Key)=$($tag.EphemeralInstanceValue) のタグがありません（実際: $($instance.Tag)）。永続 instance の可能性があるため terminate しません。"
}
elseif (Test-DesktopEphemeralInstanceGone -State $instance.State)
{
    $instance.State
}
else
{
    Invoke-AwsCli -Arguments @('ec2', 'terminate-instances', '--region', $Region, '--instance-ids', $InstanceId) | Out-Null
    'terminated'
}

$parameterName = "$JitParameterPrefix/$InstanceId"
$deleted = Invoke-AwsCli -Arguments @('ssm', 'delete-parameter', '--region', $Region, '--name', $parameterName) -AbsentErrorCode 'ParameterNotFound'
$parameterResult = if ($null -eq $deleted) { 'not-found' } else { 'deleted' }

$runnerName = Get-DesktopEphemeralRunnerName -InstanceId $InstanceId
$runner = @(
    Invoke-GhApi -Arguments @("repos/$Repository/actions/runners?per_page=100", '--paginate', '--jq', '.runners[] | {id, name, status, busy}') |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { $_ | ConvertFrom-Json } |
        Where-Object { $_.name -ceq $runnerName }
) | Select-Object -First 1

$runnerResult = if ($null -eq $runner)
{
    'not-found'
}
elseif ($runner.busy)
{
    # instance を terminate したので job はいずれ失敗し、runner は offline になる。
    # 実行中の登録を消すと job の結果を追えなくなるため、ここでは残して reaper に任せる。
    'kept-busy'
}
else
{
    Invoke-GhApi -Arguments @('-X', 'DELETE', "repos/$Repository/actions/runners/$($runner.id)") | Out-Null
    'deleted'
}

[pscustomobject]@{
    schemaVersion = 1
    instanceId    = $InstanceId
    instance      = $instanceResult
    parameter     = $parameterResult
    runnerName    = $runnerName
    runner        = $runnerResult
} | ConvertTo-Json
