<#
.SYNOPSIS
  SSM Run Command で desktop E2E runner ホストの bootstrap を投入する（#392）。
.DESCRIPTION
  Setup-DesktopRunnerHost.ps1 とそのモジュールを base64 で送り込み、instance 上で実行する。

  Run Command の commands へスクリプト本文を直接置くと、SSM Agent が書き出す一時ファイルが
  BOM 無し UTF-8 になり、Windows PowerShell 5.1 が日本語コメントを ANSI として読んで構文エラーに
  なる。base64 で渡してバイト列のまま復元することでエンコーディングを保つ。

  instance は起動済みで、SSM に登録されている必要がある
  （Initialize-DesktopRunnerInstanceProfile.ps1 を先に実行する）。
.EXAMPLE
  .\Invoke-DesktopRunnerBootstrap.ps1 -InstanceId i-0123456789abcdef0 -Region us-east-1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^i-[0-9a-f]{8,17}$')]
    [string]$InstanceId,

    [ValidateNotNullOrEmpty()]
    [string]$Region = 'us-east-1',

    [ValidateNotNullOrEmpty()]
    [string]$PasswordParameterName = '/squirrel-notifier/desktop-e2e/autologon-password',

    [ValidateNotNullOrEmpty()]
    [string]$RunnerDirectory = 'C:\Users\Administrator\actions-runner',

    [ValidateRange(60, 3600)]
    [int]$TimeoutSeconds = 600
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-Base64File
{
    param([string]$Path)

    return [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($Path))
}

$moduleBase64 = ConvertTo-Base64File -Path (Join-Path $PSScriptRoot 'DesktopRunnerHost.psm1')
$scriptBase64 = ConvertTo-Base64File -Path (Join-Path $PSScriptRoot 'Setup-DesktopRunnerHost.ps1')

# remote 側は ASCII だけで構成し、エンコーディングの影響を受けないようにする。
$remoteCommands = @(
    '$ErrorActionPreference = ''Stop'''
    # SSM Agent は stdout/stderr を UTF-8 として収集するため、既定の ANSI のままだと
    # 日本語のエラーメッセージが読めなくなる。
    '[Console]::OutputEncoding = [System.Text.Encoding]::UTF8'
    '$dir = Join-Path $env:TEMP ''squirrel-notifier-bootstrap'''
    'New-Item -ItemType Directory -Path $dir -Force | Out-Null'
    "[IO.File]::WriteAllBytes((Join-Path `$dir 'DesktopRunnerHost.psm1'), [Convert]::FromBase64String('$moduleBase64'))"
    "[IO.File]::WriteAllBytes((Join-Path `$dir 'Setup-DesktopRunnerHost.ps1'), [Convert]::FromBase64String('$scriptBase64'))"
    "& (Join-Path `$dir 'Setup-DesktopRunnerHost.ps1') -PasswordParameterName '$PasswordParameterName' -Region '$Region' -RunnerDirectory '$RunnerDirectory'"
    'Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue'
)

$parameters = @{ commands = $remoteCommands } | ConvertTo-Json -Depth 4 -Compress
$parametersPath = Join-Path ([System.IO.Path]::GetTempPath()) ("desktop-runner-bootstrap-" + [guid]::NewGuid().ToString('N') + '.json')
Set-Content -LiteralPath $parametersPath -Value $parameters -Encoding ascii

try
{
    if (-not $PSCmdlet.ShouldProcess($InstanceId, 'SSM Run Command で bootstrap を実行'))
    {
        return
    }

    $commandId = & aws ssm send-command `
        --region $Region `
        --instance-ids $InstanceId `
        --document-name 'AWS-RunPowerShellScript' `
        --comment 'squirrel-notifier desktop E2E runner bootstrap (#392)' `
        --timeout-seconds $TimeoutSeconds `
        --parameters "file://$parametersPath" `
        --query 'Command.CommandId' `
        --output text

    if ($LASTEXITCODE -ne 0)
    {
        throw "SSM Run Command の送信に失敗しました（exit code $LASTEXITCODE）。instance が SSM に登録されているか確認してください。"
    }

    $commandId = ($commandId | Out-String).Trim()
    Write-Host "command id: $commandId"

    # wait は失敗時も非 0 で返るため、状態は get-command-invocation で確定させる。
    & aws ssm wait command-executed --region $Region --command-id $commandId --instance-id $InstanceId 2>&1 | Out-Null

    $invocation = & aws ssm get-command-invocation `
        --region $Region `
        --command-id $commandId `
        --instance-id $InstanceId `
        --output json

    if ($LASTEXITCODE -ne 0)
    {
        throw "SSM Run Command の結果取得に失敗しました（exit code $LASTEXITCODE）。command id: $commandId"
    }

    $result = ($invocation | Out-String) | ConvertFrom-Json
    Write-Host "status: $($result.Status)"
    if (-not [string]::IsNullOrWhiteSpace($result.StandardOutputContent))
    {
        Write-Host '--- stdout ---'
        Write-Host $result.StandardOutputContent
    }
    if (-not [string]::IsNullOrWhiteSpace($result.StandardErrorContent))
    {
        Write-Host '--- stderr ---'
        Write-Host $result.StandardErrorContent
    }

    if ($result.Status -ne 'Success')
    {
        throw "bootstrap が成功しませんでした: $($result.Status)。command id: $commandId"
    }
}
finally
{
    Remove-Item -LiteralPath $parametersPath -Force -ErrorAction SilentlyContinue
}
