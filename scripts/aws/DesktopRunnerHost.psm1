<#
.SYNOPSIS
  desktop E2E runner ホストの bootstrap 計画を組み立てる。
.DESCRIPTION
  registry / タスクスケジューラ / セッションポリシーへ実際に書き込むのは
  Setup-DesktopRunnerHost.ps1 の責務であり、本モジュールは「何を書くか」を決めるだけの
  副作用の無い関数だけを持つ。Windows と管理者権限を必要としないため Pester で固定できる。

  Windows PowerShell 5.1（SSM Run Command の既定 shell）でも動く構文だけを使用する。
#>

Set-StrictMode -Version Latest

$script:WinlogonKeyPath = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'

function New-DesktopRunnerWinlogonPlan
{
    <#
    .SYNOPSIS
      自動ログオンの registry 計画を返す。
    .DESCRIPTION
      パスワードは LSA secret（DefaultPassword）へ格納するため、registry へは書かない。
      過去に平文で書かれていた場合に備え、DefaultPassword は常に削除対象とする。
      AutoLogonCount は残っていると自動ログオンの回数が尽きるため同じく削除する。
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$UserName,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$DomainName
    )

    return [pscustomobject]@{
        Path         = $script:WinlogonKeyPath
        SetValues    = [ordered]@{
            AutoAdminLogon    = '1'
            DefaultUserName   = $UserName
            DefaultDomainName = $DomainName
        }
        RemoveValues = @('DefaultPassword', 'AutoLogonCount')
    }
}

function New-DesktopRunnerLogonTaskXml
{
    <#
    .SYNOPSIS
      ログオン時に runner を起動するタスク定義 XML を返す。
    .DESCRIPTION
      LogonType は InteractiveToken、RunLevel は HighestAvailable で固定する。
      desktop E2E の harness は対話ログオン済み session と非 SYSTEM ユーザーを要求するため、
      service（session 0）として起動する構成を取ってはならない（#378）。
      ExecutionTimeLimit は PT0S（無制限）とし、runner が長時間待機しても打ち切られないようにする。
      run.cmd を直接起動せず Start-DesktopRunner.ps1 を経由させ、永続 runner と JIT runner の
      どちらで起動するかを instance ごとに判定させる（#380）。
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$TaskUserId,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$RunnerDirectory,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$LauncherPath,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$ConfigPath,

        [ValidateRange(0, 3600)]
        [int]$StartDelaySeconds = 30,

        [ValidateNotNullOrEmpty()]
        [string]$Description = 'EC2 起動時の対話ログオンで GitHub Actions self-hosted runner を開始する（squirrel-notifier desktop E2E）。'
    )

    $escapedUser = [System.Security.SecurityElement]::Escape($TaskUserId)
    $escapedDirectory = [System.Security.SecurityElement]::Escape($RunnerDirectory)
    $escapedDescription = [System.Security.SecurityElement]::Escape($Description)
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -ConfigPath "{1}"' -f $LauncherPath, $ConfigPath
    $escapedArguments = [System.Security.SecurityElement]::Escape($arguments)
    $delay = 'PT{0}S' -f $StartDelaySeconds

    return @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>$escapedDescription</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>$escapedUser</UserId>
      <Delay>$delay</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>$escapedUser</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
    <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>powershell.exe</Command>
      <Arguments>$escapedArguments</Arguments>
      <WorkingDirectory>$escapedDirectory</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
"@
}

function New-DesktopRunnerLaunchPlan
{
    <#
    .SYNOPSIS
      ログオン時に runner をどの方式で起動するかを決める。
    .DESCRIPTION
      AMI は永続 runner を登録済みの instance から作るため、AMI から起動した instance にも
      永続 runner の資格情報（.runner / .credentials）が残る。これを使うと複数 instance が同じ
      runner として接続し、job を取り合う。そこで bootstrap を適用した instance（LegacyInstanceId）
      だけを永続 runner とし、それ以外は資格情報を削除したうえで JIT runner として起動する（#380）。

      CurrentInstanceId と LegacyInstanceId の比較だけで決め、tag や SSM parameter の有無には依存しない。
      parameter の書き込み遅延で永続 runner へ落ちる経路を作らないためである。
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [ValidatePattern('^i-[0-9a-f]{8,17}$')]
        [string]$CurrentInstanceId,

        [Parameter(Mandatory)]
        [ValidatePattern('^i-[0-9a-f]{8,17}$')]
        [string]$LegacyInstanceId,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$RunnerDirectory,

        [Parameter(Mandatory)]
        [ValidatePattern('^/[A-Za-z0-9_.\-/]*[A-Za-z0-9_.\-]$')]
        [string]$JitParameterPrefix
    )

    if ($CurrentInstanceId -eq $LegacyInstanceId)
    {
        return [pscustomobject]@{
            Mode                    = 'persistent'
            JitParameterName        = $null
            CredentialFilesToRemove = @()
        }
    }

    # Join-Path は PowerShell provider でドライブを解決するため、Linux の Pester では C: が無く失敗する。
    # 対象は常に Windows instance 上のパスなので、区切り文字を固定して連結する。
    $runnerRoot = $RunnerDirectory.TrimEnd('\')
    $credentialFiles = @('.runner', '.credentials', '.credentials_rsaparams') |
        ForEach-Object { '{0}\{1}' -f $runnerRoot, $_ }

    return [pscustomobject]@{
        Mode                 = 'jit'
        JitParameterName     = '{0}/{1}' -f $JitParameterPrefix, $CurrentInstanceId
        CredentialFilesToRemove = @($credentialFiles)
    }
}

function New-DesktopRunnerSessionPolicyPlan
{
    <#
    .SYNOPSIS
      対話セッションを維持するための registry 値と powercfg 引数を返す。
    .DESCRIPTION
      screenshot と UI Automation は描画中の desktop を必要とする。スリープ、モニタ電源断、
      画面ロック、スクリーンセーバーはいずれも session を失わせるため無効化する。
      ユーザー個別の HKCU ではなくマシンポリシーへ書き、未ログオン状態からでも適用できるようにする。
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param()

    $registryPlans = @(
        [pscustomobject]@{
            Path      = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Personalization'
            SetValues = [ordered]@{ NoLockScreen = 1 }
            ValueKind = 'DWord'
        }
        [pscustomobject]@{
            Path      = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Control Panel\Desktop'
            SetValues = [ordered]@{
                ScreenSaveActive  = '0'
                ScreenSaverIsSecure = '0'
            }
            ValueKind = 'String'
        }
        [pscustomobject]@{
            Path      = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
            SetValues = [ordered]@{ DisableLockWorkstation = 1 }
            ValueKind = 'DWord'
        }
    )

    $powerSettings = @(
        [pscustomobject]@{ Setting = 'standby-timeout-ac'; Value = '0' }
        [pscustomobject]@{ Setting = 'monitor-timeout-ac'; Value = '0' }
        [pscustomobject]@{ Setting = 'disk-timeout-ac'; Value = '0' }
        [pscustomobject]@{ Setting = 'hibernate-timeout-ac'; Value = '0' }
    )

    return [pscustomobject]@{
        RegistryPlans = $registryPlans
        PowerSettings = $powerSettings
    }
}

function Get-DesktopRunnerInstanceProfileDecision
{
    <#
    .SYNOPSIS
      instance に付いている instance profile から、次に取るべき操作を決める。
    .DESCRIPTION
      「何かが関連付けられている」ことを関連付け済みと扱うと、別の profile が付いた instance を
      成功扱いにしてしまい、SSM role が無いまま Run Command が権限不足で失敗する。
      ARN 末尾の profile 名を期待値と照合し、状態が associated になるまでは完了と判定しない。

      CurrentArn には aws cli の `--output text` が値なしのときに返す 'None' が入り得る。
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]$CurrentArn,

        [AllowNull()]
        [AllowEmptyString()]
        [string]$CurrentState,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$ExpectedName
    )

    if ([string]::IsNullOrWhiteSpace($CurrentArn) -or $CurrentArn -eq 'None')
    {
        return [pscustomobject]@{
            Action      = 'associate'
            CurrentName = $null
            Reason      = 'instance に instance profile が関連付けられていない。'
        }
    }

    $currentName = ($CurrentArn -split '/')[-1]

    if ($currentName -ne $ExpectedName)
    {
        return [pscustomobject]@{
            Action      = 'conflict'
            CurrentName = $currentName
            Reason      = "instance には別の instance profile が関連付けられている: $CurrentArn"
        }
    }

    if ($CurrentState -eq 'associated')
    {
        return [pscustomobject]@{
            Action      = 'ok'
            CurrentName = $currentName
            Reason      = '期待する instance profile が associated で関連付けられている。'
        }
    }

    return [pscustomobject]@{
        Action      = 'wait'
        CurrentName = $currentName
        Reason      = "期待する instance profile だが状態が associated ではない: $CurrentState"
    }
}

function New-DesktopRunnerBootstrapReport
{
    <#
    .SYNOPSIS
      bootstrap の結果レポートを組み立てる。
    .DESCRIPTION
      SSM Run Command の出力は CloudWatch や API 経由で参照されるため、資格情報に類する値は
      一切含めない。パスワードは引数として受け取らず、構造的に混入し得ない形にする。
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$ComputerName,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$AutoLogonUserId,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$RunnerDirectory,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$TaskName,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$CompletedSteps,

        [string]$PasswordSource = 'unspecified'
    )

    return [pscustomobject]@{
        schemaVersion   = 1
        computerName    = $ComputerName
        autoLogonUserId = $AutoLogonUserId
        runnerDirectory = $RunnerDirectory
        taskName        = $TaskName
        passwordSource  = $PasswordSource
        completedSteps  = $CompletedSteps
        completedAtUtc  = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    }
}

Export-ModuleMember -Function @(
    'New-DesktopRunnerWinlogonPlan'
    'New-DesktopRunnerLogonTaskXml'
    'New-DesktopRunnerLaunchPlan'
    'New-DesktopRunnerSessionPolicyPlan'
    'Get-DesktopRunnerInstanceProfileDecision'
    'New-DesktopRunnerBootstrapReport'
)
