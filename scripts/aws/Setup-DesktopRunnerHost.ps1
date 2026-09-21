<#
.SYNOPSIS
  EC2 起動だけで desktop E2E runner が online になるようホストを構成する（#392）。
.DESCRIPTION
  自動ログオンを有効化し、ログオン時に GitHub Actions self-hosted runner を起動する
  タスクを登録し、対話セッションを失わせる電源・ロック設定を無効化する。

  自動ログオンのパスワードは registry へ平文で書かず、LSA secret（DefaultPassword）へ格納する。
  パスワードの出所は SSM Parameter Store の SecureString を既定とし、RDP から手で流す場合のみ
  -Password を使う。

  runner を Windows service として登録してはならない。desktop E2E の harness は
  [Environment]::UserInteractive と非 SYSTEM ユーザーを要求するため、session 0 では実行できない
  （#378、tests/e2e/scripts/Invoke-DesktopE2E.ps1）。service が登録済みの場合は停止する。

  SSM Run Command（AWS-RunPowerShellScript）から SYSTEM 権限で実行することを想定し、
  Windows PowerShell 5.1 で動く構文だけを使う。
.EXAMPLE
  .\Setup-DesktopRunnerHost.ps1 -PasswordParameterName /squirrel-notifier/desktop-e2e/autologon-password -Region us-east-1
.EXAMPLE
  .\Setup-DesktopRunnerHost.ps1 -Password (Read-Host -AsSecureString '自動ログオンするユーザーのパスワード')
#>
[CmdletBinding(SupportsShouldProcess, DefaultParameterSetName = 'SsmParameter')]
param(
    [Parameter(Mandatory, ParameterSetName = 'SsmParameter')]
    [ValidateNotNullOrEmpty()]
    [string]$PasswordParameterName,

    [Parameter(ParameterSetName = 'SsmParameter')]
    [ValidateNotNullOrEmpty()]
    [string]$Region,

    [Parameter(Mandatory, ParameterSetName = 'DirectPassword')]
    [ValidateNotNull()]
    [securestring]$Password,

    [ValidateNotNullOrEmpty()]
    [string]$RunnerDirectory = 'C:\Users\Administrator\actions-runner',

    [ValidateNotNullOrEmpty()]
    [string]$AutoLogonUserName = 'Administrator',

    [ValidateNotNullOrEmpty()]
    [string]$TaskName = 'GitHubActionsRunner-SquirrelNotifierDesktop',

    [ValidateRange(0, 3600)]
    [int]$StartDelaySeconds = 30,

    [string]$ReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'DesktopRunnerHost.psm1') -Force

$lsaSource = @'
using System;
using System.Runtime.InteropServices;

public static class SquirrelNotifierLsaSecret
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LSA_UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LSA_OBJECT_ATTRIBUTES
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint LsaOpenPolicy(
        IntPtr SystemName,
        ref LSA_OBJECT_ATTRIBUTES ObjectAttributes,
        uint DesiredAccess,
        out IntPtr PolicyHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint LsaStorePrivateData(
        IntPtr PolicyHandle,
        ref LSA_UNICODE_STRING KeyName,
        ref LSA_UNICODE_STRING PrivateData);

    [DllImport("advapi32.dll")]
    private static extern uint LsaClose(IntPtr PolicyHandle);

    [DllImport("advapi32.dll")]
    private static extern int LsaNtStatusToWinError(uint status);

    private const uint POLICY_CREATE_SECRET = 0x00000020;

    private static LSA_UNICODE_STRING ToUnicodeString(string value)
    {
        LSA_UNICODE_STRING result = new LSA_UNICODE_STRING();
        result.Buffer = Marshal.StringToHGlobalUni(value);
        result.Length = (ushort)(value.Length * 2);
        result.MaximumLength = (ushort)((value.Length + 1) * 2);
        return result;
    }

    private static void Free(LSA_UNICODE_STRING value)
    {
        if (value.Buffer != IntPtr.Zero)
        {
            Marshal.ZeroFreeGlobalAllocUnicode(value.Buffer);
        }
    }

    public static void Store(string keyName, string secret)
    {
        LSA_OBJECT_ATTRIBUTES attributes = new LSA_OBJECT_ATTRIBUTES();
        attributes.Length = Marshal.SizeOf(typeof(LSA_OBJECT_ATTRIBUTES));

        IntPtr policyHandle;
        uint status = LsaOpenPolicy(IntPtr.Zero, ref attributes, POLICY_CREATE_SECRET, out policyHandle);
        if (status != 0)
        {
            throw new InvalidOperationException(
                "LsaOpenPolicy に失敗しました。Win32 error: " + LsaNtStatusToWinError(status));
        }

        LSA_UNICODE_STRING key = ToUnicodeString(keyName);
        LSA_UNICODE_STRING data = ToUnicodeString(secret);
        try
        {
            status = LsaStorePrivateData(policyHandle, ref key, ref data);
            if (status != 0)
            {
                throw new InvalidOperationException(
                    "LsaStorePrivateData に失敗しました。Win32 error: " + LsaNtStatusToWinError(status));
            }
        }
        finally
        {
            Free(key);
            Free(data);
            LsaClose(policyHandle);
        }
    }
}
'@

function Assert-Administrator
{
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator))
    {
        throw '管理者権限が必要です。SSM Run Command または管理者 PowerShell から実行してください。'
    }
}

function Assert-RunnerDirectory
{
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container))
    {
        throw "runner ディレクトリが見つかりません: $Path"
    }

    $entryPoint = Join-Path $Path 'run.cmd'
    if (-not (Test-Path -LiteralPath $entryPoint -PathType Leaf))
    {
        throw "run.cmd が見つかりません: $entryPoint。runner の config.cmd を先に完了させてください。"
    }
}

function Assert-RunnerNotInstalledAsService
{
    $services = @(Get-Service -Name 'actions.runner.*' -ErrorAction SilentlyContinue)
    if ($services.Count -gt 0)
    {
        $names = ($services | ForEach-Object { $_.Name }) -join ', '
        throw "runner が Windows service として登録されています: $names。desktop E2E は対話ログオン済み session を要求するため、service を削除してから再実行してください（#378）。"
    }
}

function Get-SsmParameterCmdlet
{
    <#
    .SYNOPSIS
      Get-SSMParameter が使える状態なら、その CommandInfo を返す。
    .DESCRIPTION
      Windows Server の AWS AMI には AWS Tools for PowerShell がプリインストールされている一方、
      AWS CLI は入っていない。モジュールは自動読み込みされないことがあるため明示的に import する。
    #>
    $command = Get-Command -Name 'Get-SSMParameter' -ErrorAction SilentlyContinue
    if ($null -ne $command)
    {
        return $command
    }

    Import-Module -Name 'AWS.Tools.SimpleSystemsManagement' -ErrorAction SilentlyContinue
    return (Get-Command -Name 'Get-SSMParameter' -ErrorAction SilentlyContinue)
}

function Get-AwsCliPath
{
    $command = Get-Command -Name 'aws' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command)
    {
        return $command.Source
    }

    $fallback = Join-Path $env:ProgramFiles 'Amazon\AWSCLIV2\aws.exe'
    if (Test-Path -LiteralPath $fallback -PathType Leaf)
    {
        return $fallback
    }

    return $null
}

function Get-PasswordFromSsm
{
    param(
        [string]$ParameterName,
        [string]$RegionName
    )

    $cmdlet = Get-SsmParameterCmdlet
    if ($null -ne $cmdlet)
    {
        $arguments = @{ Name = $ParameterName; WithDecryption = $true }
        if (-not [string]::IsNullOrWhiteSpace($RegionName))
        {
            $arguments['Region'] = $RegionName
        }

        $parameter = Get-SSMParameter @arguments
        if ($null -eq $parameter -or [string]::IsNullOrEmpty($parameter.Value))
        {
            throw "SSM Parameter Store の値が空です: $ParameterName"
        }

        return $parameter.Value
    }

    $awsCli = Get-AwsCliPath
    if ($null -eq $awsCli)
    {
        throw 'SSM Parameter Store から取得できません。AWS Tools for PowerShell と AWS CLI のどちらも見つかりませんでした。'
    }

    $cliArguments = @('ssm', 'get-parameter', '--name', $ParameterName, '--with-decryption', '--query', 'Parameter.Value', '--output', 'text')
    if (-not [string]::IsNullOrWhiteSpace($RegionName))
    {
        $cliArguments += @('--region', $RegionName)
    }

    $value = & $awsCli @cliArguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "SSM Parameter Store からの取得に失敗しました: $ParameterName（exit code $LASTEXITCODE）"
    }

    $value = ($value | Out-String).Trim()
    if ([string]::IsNullOrEmpty($value))
    {
        throw "SSM Parameter Store の値が空です: $ParameterName"
    }

    return $value
}

function ConvertTo-PlainText
{
    param([securestring]$Secure)

    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try
    {
        return [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally
    {
        [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

function Set-RegistryPlan
{
    param(
        [string]$Path,
        $SetValues,
        [string]$ValueKind = 'String',
        [string[]]$RemoveValues = @()
    )

    if (-not (Test-Path -LiteralPath $Path))
    {
        New-Item -Path $Path -Force | Out-Null
    }

    foreach ($name in $SetValues.Keys)
    {
        New-ItemProperty -LiteralPath $Path -Name $name -Value $SetValues[$name] -PropertyType $ValueKind -Force | Out-Null
    }

    foreach ($name in $RemoveValues)
    {
        Remove-ItemProperty -LiteralPath $Path -Name $name -ErrorAction SilentlyContinue
    }
}

Assert-Administrator
Assert-RunnerDirectory -Path $RunnerDirectory
Assert-RunnerNotInstalledAsService

$computerName = $env:COMPUTERNAME
$autoLogonUserId = '{0}\{1}' -f $computerName, $AutoLogonUserName
$completedSteps = New-Object System.Collections.Generic.List[string]

if ($PSCmdlet.ParameterSetName -eq 'SsmParameter')
{
    $passwordSource = 'ssm-parameter-store'
    $plainPassword = Get-PasswordFromSsm -ParameterName $PasswordParameterName -RegionName $Region
}
else
{
    $passwordSource = 'direct-securestring'
    $plainPassword = ConvertTo-PlainText -Secure $Password
}

try
{
    if ($PSCmdlet.ShouldProcess('LSA secret: DefaultPassword', '自動ログオン用パスワードを格納'))
    {
        Add-Type -TypeDefinition $lsaSource -Language CSharp
        [SquirrelNotifierLsaSecret]::Store('DefaultPassword', $plainPassword)
        $completedSteps.Add('lsa-secret')
    }
}
finally
{
    $plainPassword = $null
    [System.GC]::Collect()
}

$winlogonPlan = New-DesktopRunnerWinlogonPlan -UserName $AutoLogonUserName -DomainName $computerName
if ($PSCmdlet.ShouldProcess($winlogonPlan.Path, '自動ログオンを有効化（平文パスワードは書かない）'))
{
    Set-RegistryPlan -Path $winlogonPlan.Path -SetValues $winlogonPlan.SetValues -ValueKind 'String' -RemoveValues $winlogonPlan.RemoveValues
    $completedSteps.Add('winlogon-registry')
}

$taskXml = New-DesktopRunnerLogonTaskXml -TaskUserId $autoLogonUserId -RunnerDirectory $RunnerDirectory -StartDelaySeconds $StartDelaySeconds
if ($PSCmdlet.ShouldProcess($TaskName, 'ログオン時に runner を起動するタスクを登録'))
{
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Register-ScheduledTask -TaskName $TaskName -Xml $taskXml | Out-Null
    $completedSteps.Add('logon-task')
}

$sessionPolicy = New-DesktopRunnerSessionPolicyPlan
foreach ($plan in $sessionPolicy.RegistryPlans)
{
    if ($PSCmdlet.ShouldProcess($plan.Path, '対話セッション維持のためのポリシーを適用'))
    {
        Set-RegistryPlan -Path $plan.Path -SetValues $plan.SetValues -ValueKind $plan.ValueKind
    }
}
foreach ($powerSetting in $sessionPolicy.PowerSettings)
{
    if ($PSCmdlet.ShouldProcess("$($powerSetting.Setting) = $($powerSetting.Value)", 'powercfg を適用'))
    {
        & powercfg.exe '/change' $powerSetting.Setting $powerSetting.Value | Out-Null
        if ($LASTEXITCODE -ne 0)
        {
            throw "powercfg の適用に失敗しました: $($powerSetting.Setting)（exit code $LASTEXITCODE）"
        }
    }
}
$completedSteps.Add('session-policy')

$report = New-DesktopRunnerBootstrapReport `
    -ComputerName $computerName `
    -AutoLogonUserId $autoLogonUserId `
    -RunnerDirectory $RunnerDirectory `
    -TaskName $TaskName `
    -CompletedSteps $completedSteps.ToArray() `
    -PasswordSource $passwordSource

$reportJson = $report | ConvertTo-Json -Depth 4
if (-not [string]::IsNullOrWhiteSpace($ReportPath))
{
    Set-Content -LiteralPath $ReportPath -Value $reportJson -Encoding UTF8
}

Write-Output $reportJson
