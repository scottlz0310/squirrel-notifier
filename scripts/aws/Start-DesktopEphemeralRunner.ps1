<#
.SYNOPSIS
  Launch Template から使い捨て desktop E2E instance を起動し、JIT runner が online になるまで待つ（#380）。
.DESCRIPTION
  desktop-e2e.yml の prepare-runner（runner_mode: ephemeral）から呼ぶ。

  1. Launch Template の default version で instance を 1 台起動する（値は上書きしない）
  2. instance に ephemeral-runner タグが付いたことを確かめる（付いていなければ cleanup で terminate できない）
  3. instance ID から runner 名を決めて JIT config を発行する。ラベルは run 固有のもので、永続 runner の
     ラベルは付けない
  4. JIT config を SSM Parameter Store に SecureString で置く（Advanced tier、有効期限つき）。
     instance 上の Start-DesktopRunner.ps1 がこれを待って ephemeral runner として起動する
  5. runner が online になるまで待つ

  -GitHubOutputPath を指定すると、instance_id / runner_name / runner_label を分かった時点で書き出す。
  後続の手順で失敗しても、cleanup が terminate する instance を特定できるようにするため。

  JIT config の値はログへ出力しない。parameter へはコマンドラインに載らないよう一時ファイル経由で渡し、
  書き込み後に削除する。

  aws CLI（OIDC ロールの資格情報）と gh CLI（GH_TOKEN に Administration: write の App token）を使う。
.EXAMPLE
  .\Start-DesktopEphemeralRunner.ps1 -Repository scottlz0310/squirrel-notifier -LaunchTemplateId lt-09e208553b742f6c1 -RunId 123
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository,

    [Parameter(Mandatory)]
    [ValidatePattern('^lt-[0-9a-f]{8,17}$')]
    [string]$LaunchTemplateId,

    [Parameter(Mandatory)]
    [string]$RunId,

    [ValidateNotNullOrEmpty()]
    [string]$Region = 'us-east-1',

    [ValidatePattern('^(/[A-Za-z0-9_.-]+){2,}$')]
    [string]$JitParameterPrefix = '/squirrel-notifier/desktop-e2e/jit',

    # reaper の回収（起動から 120 分）と揃える。
    [ValidateRange(30, 1440)]
    [int]$ParameterExpirationMinutes = 120,

    # 手動検証（2026-09-23）では起動から online まで約 7 分だった。
    [ValidateRange(60, 3600)]
    [int]$RunnerWaitSeconds = 900,

    [ValidateRange(1, 60)]
    [int]$PollSeconds = 10,

    [string]$GitHubOutputPath,

    [ValidateNotNullOrEmpty()]
    [string]$TempDirectory = [System.IO.Path]::GetTempPath()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'DesktopEphemeralRunner.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'DesktopRunnerImage.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'DesktopE2ECli.psm1') -Force

function Write-StepOutput
{
    param(
        [string]$Name,
        [string]$Value
    )

    if ($GitHubOutputPath)
    {
        Add-Content -LiteralPath $GitHubOutputPath -Value "$Name=$Value" -Encoding utf8
    }
}

function Get-InstanceTagValue
{
    <#
    .DESCRIPTION
      起動直後の instance は Describe に反映されるまで InvalidInstanceID.NotFound を返すことがあるため、
      NotFound の間だけ再試行する。
    #>
    param(
        [string]$InstanceId,
        [string]$Key
    )

    for ($attempt = 1; $attempt -le 12; $attempt++)
    {
        $value = Invoke-AwsCli -Arguments @(
            'ec2', 'describe-instances',
            '--region', $Region,
            '--instance-ids', $InstanceId,
            '--query', "Reservations[0].Instances[0].Tags[?Key=='$Key'].Value | [0]",
            '--output', 'text'
        ) -AbsentErrorCode 'InvalidInstanceID.NotFound'

        if ($null -ne $value)
        {
            return $value
        }

        Start-Sleep -Seconds $PollSeconds
    }

    throw "起動した instance が Describe に現れません: $InstanceId"
}

$tag = Get-DesktopRunnerResourceTag
$label = Get-DesktopEphemeralRunnerLabel -RunId $RunId
Write-StepOutput -Name 'runner_label' -Value $label

$instanceId = Invoke-AwsCli -Arguments @(
    'ec2', 'run-instances',
    '--region', $Region,
    '--count', '1',
    '--launch-template', "LaunchTemplateId=$LaunchTemplateId,Version=`$Default",
    '--query', 'Instances[0].InstanceId',
    '--output', 'text'
)
if ($instanceId -cnotmatch '^i-[0-9a-f]{8,17}$')
{
    throw "run-instances の応答から instance ID を読めません: $instanceId"
}
Write-StepOutput -Name 'instance_id' -Value $instanceId
Write-Verbose "使い捨て instance を起動しました: $instanceId"

$tagValue = Get-InstanceTagValue -InstanceId $instanceId -Key $tag.Key
if ($tagValue -cne $tag.EphemeralInstanceValue)
{
    throw "起動した instance $instanceId に $($tag.Key)=$($tag.EphemeralInstanceValue) のタグがありません（実際: $tagValue）。Launch Template のタグ指定を確認してください。この instance は OIDC ロールでは terminate できないため、Admin で削除してください。"
}

$request = New-DesktopEphemeralJitConfigRequest -InstanceId $instanceId -RunId $RunId
$runnerName = $request.name
Write-StepOutput -Name 'runner_name' -Value $runnerName

$jit = (Invoke-GhApi -Arguments @('-X', 'POST', "repos/$Repository/actions/runners/generate-jitconfig") -InputJson ($request | ConvertTo-Json -Compress)) -join "`n" | ConvertFrom-Json
if ([string]::IsNullOrEmpty($jit.encoded_jit_config))
{
    throw "generate-jitconfig の応答に encoded_jit_config がありません（runner: $runnerName）。"
}

$parameterName = "$JitParameterPrefix/$instanceId"
$expiresAt = [datetimeoffset]::UtcNow.AddMinutes($ParameterExpirationMinutes)
$inputPath = Join-Path $TempDirectory "desktop-e2e-jit-$instanceId.json"
try
{
    # 値を書く前に所有者だけが読める状態にする。
    New-Item -ItemType File -Path $inputPath -Force | Out-Null
    if ($IsLinux -or $IsMacOS)
    {
        & chmod 600 $inputPath
    }

    $parameterRequest = New-DesktopEphemeralJitParameterRequest -Name $parameterName -Value $jit.encoded_jit_config -ExpiresAt $expiresAt
    [System.IO.File]::WriteAllText($inputPath, ($parameterRequest | ConvertTo-Json -Compress), [System.Text.UTF8Encoding]::new($false))
    Invoke-AwsCli -Arguments @('ssm', 'put-parameter', '--region', $Region, '--cli-input-json', "file://$inputPath") | Out-Null
}
finally
{
    Remove-Item -LiteralPath $inputPath -Force -ErrorAction SilentlyContinue
    $jit = $null
    $parameterRequest = $null
}

$deadline = [datetimeoffset]::UtcNow.AddSeconds($RunnerWaitSeconds)
$state = 'missing'
while ([datetimeoffset]::UtcNow -lt $deadline)
{
    $runners = @(
        Invoke-GhApi -Arguments @("repos/$Repository/actions/runners?per_page=100", '--paginate', '--jq', '.runners[] | {id, name, status, busy, labels: [.labels[] | {name}]}') |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            ForEach-Object { $_ | ConvertFrom-Json }
    )
    $state = Get-DesktopEphemeralRunnerState -Runners $runners -RunnerName $runnerName -Label $label

    if ($state -eq 'online')
    {
        break
    }

    if ($state -eq 'invalid-label')
    {
        throw "runner $runnerName に run 固有のラベル $label がありません。"
    }

    $instanceState = Invoke-AwsCli -Arguments @(
        'ec2', 'describe-instances',
        '--region', $Region,
        '--instance-ids', $instanceId,
        '--query', 'Reservations[0].Instances[0].State.Name',
        '--output', 'text'
    )
    if (Test-DesktopEphemeralInstanceGone -State $instanceState)
    {
        throw "runner が online になる前に instance $instanceId が $instanceState になりました。"
    }

    Write-Verbose "runner $runnerName は $state です（instance: $instanceState）。"
    Start-Sleep -Seconds $PollSeconds
}

if ($state -ne 'online')
{
    throw "runner $runnerName が ${RunnerWaitSeconds} 秒以内に online になりませんでした（最後の状態: $state）。"
}

[pscustomobject]@{
    schemaVersion = 1
    instanceId    = $instanceId
    runnerName    = $runnerName
    runnerLabel   = $label
    parameterName = $parameterName
    expiresAt     = $expiresAt.ToString('o')
} | ConvertTo-Json
