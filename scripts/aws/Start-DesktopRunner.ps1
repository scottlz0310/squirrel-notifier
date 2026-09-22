<#
.SYNOPSIS
  ログオン時に desktop E2E runner を起動する（#380）。
.DESCRIPTION
  Setup-DesktopRunnerHost.ps1 が登録するログオンタスクから、自動ログオンした対話 session で実行される。

  - bootstrap を適用した instance（永続 runner）では、従来どおり run.cmd を起動する。
  - AMI から起動した使い捨て instance では、AMI に焼き込まれた永続 runner の資格情報を削除し、
    workflow が SSM Parameter Store へ置いた JIT config を待ってから ephemeral runner として起動する。

  JIT config は ACTIONS_RUNNER_INPUT_JITCONFIG 環境変数で渡す。コマンドライン引数に載せると
  同じ instance 上の任意のプロセスから参照できるため避ける。runner は読み取り後にこの変数を消去する。
  JIT config の値はログへ出力しない。

  Windows PowerShell 5.1 で動く構文だけを使う。
.EXAMPLE
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File Start-DesktopRunner.ps1 -ConfigPath C:\ProgramData\SquirrelNotifier\desktop-runner\launcher.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ConfigPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'DesktopRunnerHost.psm1') -Force

$logPath = Join-Path (Split-Path -Parent $ConfigPath) 'launcher.log'

function Write-LauncherLog
{
    param([string]$Message)

    $line = '{0} {1}' -f (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'), $Message
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
}

function Get-CurrentInstanceId
{
    <#
    .DESCRIPTION
      ログオン直後はネットワークが未初期化のことがあるため、IMDSv2 の取得を再試行する。
    #>
    $lastError = $null
    for ($attempt = 1; $attempt -le 24; $attempt++)
    {
        try
        {
            $token = Invoke-RestMethod -Method Put -Uri 'http://169.254.169.254/latest/api/token' `
                -Headers @{ 'X-aws-ec2-metadata-token-ttl-seconds' = '300' } -TimeoutSec 5
            return Invoke-RestMethod -Method Get -Uri 'http://169.254.169.254/latest/meta-data/instance-id' `
                -Headers @{ 'X-aws-ec2-metadata-token' = $token } -TimeoutSec 5
        }
        catch
        {
            $lastError = $_
            Start-Sleep -Seconds 5
        }
    }

    throw "IMDS から instance ID を取得できませんでした: $lastError"
}

function Test-ParameterNotFound
{
    param([System.Exception]$Exception)

    for ($current = $Exception; $null -ne $current; $current = $current.InnerException)
    {
        if ($current.GetType().Name -eq 'ParameterNotFoundException')
        {
            return $true
        }
    }

    return $false
}

function Wait-JitConfig
{
    <#
    .DESCRIPTION
      parameter が未作成の間だけ待つ。権限不足などそれ以外の失敗は待っても解消しないため即座に失敗させる。
    #>
    param(
        [string]$ParameterName,
        [string]$RegionName,
        [int]$TimeoutSeconds
    )

    Import-Module -Name 'AWS.Tools.SimpleSystemsManagement'

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline)
    {
        try
        {
            $parameter = Get-SSMParameter -Name $ParameterName -WithDecryption $true -Region $RegionName
            if ([string]::IsNullOrEmpty($parameter.Value))
            {
                throw "JIT config の parameter が空です: $ParameterName"
            }

            return $parameter.Value
        }
        catch
        {
            if (-not (Test-ParameterNotFound -Exception $_.Exception))
            {
                throw
            }
        }

        Start-Sleep -Seconds 10
    }

    throw "JIT config が ${TimeoutSeconds} 秒以内に置かれませんでした: $ParameterName"
}

function Invoke-RunnerProcess
{
    param([string]$RunnerDirectory)

    Push-Location -LiteralPath $RunnerDirectory
    try
    {
        & cmd.exe /c run.cmd
        return $LASTEXITCODE
    }
    finally
    {
        Pop-Location
    }
}

try
{
    $config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $currentInstanceId = Get-CurrentInstanceId

    $plan = New-DesktopRunnerLaunchPlan `
        -CurrentInstanceId $currentInstanceId `
        -LegacyInstanceId $config.legacyInstanceId `
        -RunnerDirectory $config.runnerDirectory `
        -JitParameterPrefix $config.jitParameterPrefix
    Write-LauncherLog "instance=$currentInstanceId mode=$($plan.Mode)"

    if ($plan.Mode -eq 'persistent')
    {
        $exitCode = Invoke-RunnerProcess -RunnerDirectory $config.runnerDirectory
        Write-LauncherLog "runner exited: $exitCode"
        exit $exitCode
    }

    foreach ($path in $plan.CredentialFilesToRemove)
    {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
    Write-LauncherLog '永続 runner の資格情報を削除しました。JIT config を待機します。'

    $jitConfig = Wait-JitConfig -ParameterName $plan.JitParameterName -RegionName $config.region -TimeoutSeconds $config.jitWaitSeconds
    Write-LauncherLog "JIT config を取得しました: $($plan.JitParameterName)"

    $env:ACTIONS_RUNNER_INPUT_JITCONFIG = $jitConfig
    $jitConfig = $null
    try
    {
        $exitCode = Invoke-RunnerProcess -RunnerDirectory $config.runnerDirectory
    }
    finally
    {
        Remove-Item -LiteralPath 'Env:ACTIONS_RUNNER_INPUT_JITCONFIG' -ErrorAction SilentlyContinue
    }

    Write-LauncherLog "runner exited: $exitCode"
    exit $exitCode
}
catch
{
    Write-LauncherLog "失敗: $($_.Exception.Message)"
    throw
}
