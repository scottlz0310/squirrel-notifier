<#
.SYNOPSIS
  desktop E2E runner の EC2 instance を SSM 管理下へ置くための IAM リソースを作成する（#392）。
.DESCRIPTION
  instance profile が無い間は SSM Agent が登録されず、Run Command で bootstrap を投入できない。
  本スクリプトは role、inline policy、instance profile を冪等に作成し、対象 instance へ関連付ける。

  付与する権限は次の 2 つに限定する。
  - AmazonSSMManagedInstanceCore（Run Command と Session Manager の最小セット）
  - 自動ログオン用パスワードを置く 1 つの SSM parameter と、使い捨て instance 向けの
    JIT config を置く prefix 配下（#380）に対する読み取りと、SSM 経由に限定した kms:Decrypt

  AWS 側の構成はこれまでリポジトリに記録が無かった。#380 で instance を使い捨てへ移行するときも
  同じ role を再利用できるよう、本スクリプトを唯一の手順とする。
.EXAMPLE
  .\Initialize-DesktopRunnerInstanceProfile.ps1 -InstanceId i-0123456789abcdef0 -Region us-east-1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^i-[0-9a-f]{8,17}$')]
    [string]$InstanceId,

    [ValidateNotNullOrEmpty()]
    [string]$Region = 'us-east-1',

    [ValidateNotNullOrEmpty()]
    [string]$RoleName = 'SquirrelNotifierDesktopE2ERunner',

    [ValidateNotNullOrEmpty()]
    [string]$InstanceProfileName = 'SquirrelNotifierDesktopE2ERunner',

    [ValidateNotNullOrEmpty()]
    [string]$PasswordParameterName = '/squirrel-notifier/desktop-e2e/autologon-password',

    [ValidateNotNullOrEmpty()]
    [string]$JitParameterPrefix = '/squirrel-notifier/desktop-e2e/jit'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'DesktopRunnerHost.psm1') -Force

$inlinePolicyName = 'DesktopE2EAutoLogonParameterRead'

function Invoke-AwsCli
{
    <#
    .SYNOPSIS
      aws CLI を呼び出し、失敗を例外へ変換する。
    .DESCRIPTION
      -AllowFailure は「存在しない」を戻り値で判定したい場合だけに使う。
      それ以外の失敗を握り潰すと、権限不足を設定済みと誤認する。
    #>
    param(
        [string[]]$Arguments,
        [switch]$AllowFailure
    )

    $output = & aws @Arguments 2>&1
    if ($LASTEXITCODE -ne 0)
    {
        if ($AllowFailure)
        {
            return $null
        }

        throw "aws CLI が失敗しました: aws $($Arguments -join ' ')`n$output"
    }

    return ($output | Out-String).Trim()
}

function New-TemporaryJsonFile
{
    param([string]$Content)

    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("desktop-runner-" + [guid]::NewGuid().ToString('N') + '.json')
    Set-Content -LiteralPath $path -Value $Content -Encoding ascii
    return $path
}

$accountId = Invoke-AwsCli -Arguments @('sts', 'get-caller-identity', '--query', 'Account', '--output', 'text')
Write-Verbose "AWS account: $accountId"

$parameterArn = "arn:aws:ssm:${Region}:${accountId}:parameter${PasswordParameterName}"
$jitParameterArn = "arn:aws:ssm:${Region}:${accountId}:parameter${JitParameterPrefix}/*"

$trustPolicy = @'
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Principal": { "Service": "ec2.amazonaws.com" },
      "Action": "sts:AssumeRole"
    }
  ]
}
'@

$inlinePolicy = @"
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "ReadAutoLogonPassword",
      "Effect": "Allow",
      "Action": "ssm:GetParameter",
      "Resource": "$parameterArn"
    },
    {
      "Sid": "ReadJitRunnerConfig",
      "Effect": "Allow",
      "Action": "ssm:GetParameter",
      "Resource": "$jitParameterArn"
    },
    {
      "Sid": "DecryptViaSsmOnly",
      "Effect": "Allow",
      "Action": "kms:Decrypt",
      "Resource": "*",
      "Condition": {
        "StringEquals": { "kms:ViaService": "ssm.$Region.amazonaws.com" }
      }
    }
  ]
}
"@

$existingRole = Invoke-AwsCli -Arguments @('iam', 'get-role', '--role-name', $RoleName, '--query', 'Role.RoleName', '--output', 'text') -AllowFailure
if ($null -eq $existingRole)
{
    if ($PSCmdlet.ShouldProcess($RoleName, 'IAM role を作成'))
    {
        $trustPolicyPath = New-TemporaryJsonFile -Content $trustPolicy
        try
        {
            Invoke-AwsCli -Arguments @(
                'iam', 'create-role',
                '--role-name', $RoleName,
                '--assume-role-policy-document', "file://$trustPolicyPath",
                '--description', 'squirrel-notifier desktop E2E runner instance role (#392)'
            ) | Out-Null
        }
        finally
        {
            Remove-Item -LiteralPath $trustPolicyPath -Force -ErrorAction SilentlyContinue
        }
    }
}
else
{
    Write-Verbose "IAM role は作成済みです: $RoleName"
}

if ($PSCmdlet.ShouldProcess($RoleName, 'AmazonSSMManagedInstanceCore をアタッチ'))
{
    Invoke-AwsCli -Arguments @(
        'iam', 'attach-role-policy',
        '--role-name', $RoleName,
        '--policy-arn', 'arn:aws:iam::aws:policy/AmazonSSMManagedInstanceCore'
    ) | Out-Null
}

if ($PSCmdlet.ShouldProcess($RoleName, "inline policy $inlinePolicyName を適用"))
{
    $inlinePolicyPath = New-TemporaryJsonFile -Content $inlinePolicy
    try
    {
        Invoke-AwsCli -Arguments @(
            'iam', 'put-role-policy',
            '--role-name', $RoleName,
            '--policy-name', $inlinePolicyName,
            '--policy-document', "file://$inlinePolicyPath"
        ) | Out-Null
    }
    finally
    {
        Remove-Item -LiteralPath $inlinePolicyPath -Force -ErrorAction SilentlyContinue
    }
}

$existingProfile = Invoke-AwsCli -Arguments @(
    'iam', 'get-instance-profile',
    '--instance-profile-name', $InstanceProfileName,
    '--query', 'InstanceProfile.Roles[0].RoleName',
    '--output', 'text'
) -AllowFailure

if ($null -eq $existingProfile)
{
    if ($PSCmdlet.ShouldProcess($InstanceProfileName, 'instance profile を作成'))
    {
        Invoke-AwsCli -Arguments @('iam', 'create-instance-profile', '--instance-profile-name', $InstanceProfileName) | Out-Null
    }
}

if ($existingProfile -ne $RoleName)
{
    if ($PSCmdlet.ShouldProcess($InstanceProfileName, "role $RoleName を instance profile へ追加"))
    {
        Invoke-AwsCli -Arguments @(
            'iam', 'add-role-to-instance-profile',
            '--instance-profile-name', $InstanceProfileName,
            '--role-name', $RoleName
        ) | Out-Null
    }
}

function Get-CurrentInstanceProfileAssociation
{
    <#
    .SYNOPSIS
      instance に現在関連付けられている instance profile の ARN と状態を返す。
    #>
    param(
        [string]$TargetInstanceId,
        [string]$RegionName
    )

    $raw = Invoke-AwsCli -Arguments @(
        'ec2', 'describe-iam-instance-profile-associations',
        '--region', $RegionName,
        '--filters', "Name=instance-id,Values=$TargetInstanceId", 'Name=state,Values=associating,associated',
        '--query', 'IamInstanceProfileAssociations[0].[IamInstanceProfile.Arn,State]',
        '--output', 'text'
    )

    $fields = @(($raw -split '\s+') | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $arn = if ($fields.Count -ge 1) { $fields[0] } else { '' }
    $state = if ($fields.Count -ge 2) { $fields[1] } else { '' }

    return [pscustomobject]@{ Arn = $arn; State = $state }
}

$association = Get-CurrentInstanceProfileAssociation -TargetInstanceId $InstanceId -RegionName $Region
$decision = Get-DesktopRunnerInstanceProfileDecision `
    -CurrentArn $association.Arn `
    -CurrentState $association.State `
    -ExpectedName $InstanceProfileName

if ($decision.Action -eq 'conflict')
{
    throw "$($decision.Reason) 期待する profile は $InstanceProfileName です。desktop E2E runner 以外の用途で使われている instance か確認し、必要なら手動で付け替えてから再実行してください。"
}

if ($decision.Action -eq 'associate')
{
    if ($PSCmdlet.ShouldProcess($InstanceId, "instance profile $InstanceProfileName を関連付け"))
    {
        # IAM は結果整合性のため、作成直後の instance profile は EC2 から
        # InvalidParameterValue として見える。伝播するまで再試行する。
        $maxAttempts = 12
        $succeeded = $false
        for ($attempt = 1; $attempt -le $maxAttempts; $attempt++)
        {
            $result = Invoke-AwsCli -Arguments @(
                'ec2', 'associate-iam-instance-profile',
                '--region', $Region,
                '--instance-id', $InstanceId,
                '--iam-instance-profile', "Name=$InstanceProfileName"
            ) -AllowFailure

            if ($null -ne $result)
            {
                $succeeded = $true
                break
            }

            Write-Verbose "instance profile がまだ伝播していません。再試行します ($attempt/$maxAttempts)。"
            Start-Sleep -Seconds 5
        }

        if (-not $succeeded)
        {
            throw "instance profile $InstanceProfileName を $InstanceId へ関連付けできませんでした。IAM の伝播を待ってから再実行してください。"
        }
    }
}

# associating のまま成功を返すと、SSM への登録前に bootstrap を投入して権限不足で失敗する。
if ($PSCmdlet.ShouldProcess($InstanceId, "instance profile $InstanceProfileName が associated になるまで待機"))
{
    $maxWaitAttempts = 12
    $associationState = ''
    for ($attempt = 1; $attempt -le $maxWaitAttempts; $attempt++)
    {
        $association = Get-CurrentInstanceProfileAssociation -TargetInstanceId $InstanceId -RegionName $Region
        $decision = Get-DesktopRunnerInstanceProfileDecision `
            -CurrentArn $association.Arn `
            -CurrentState $association.State `
            -ExpectedName $InstanceProfileName

        if ($decision.Action -eq 'conflict')
        {
            throw "$($decision.Reason) 期待する profile は $InstanceProfileName です。"
        }

        if ($decision.Action -eq 'ok')
        {
            $associationState = $association.State
            break
        }

        Write-Verbose "instance profile の関連付けがまだ完了していません: $($decision.Reason)（$attempt/$maxWaitAttempts）"
        Start-Sleep -Seconds 5
    }

    if ($associationState -ne 'associated')
    {
        throw "instance profile $InstanceProfileName が $InstanceId で associated になりませんでした。最後に観測した状態: '$($association.State)'"
    }
}

[pscustomobject]@{
    schemaVersion         = 1
    accountId             = $accountId
    region                = $Region
    instanceId            = $InstanceId
    roleName              = $RoleName
    instanceProfileName   = $InstanceProfileName
    instanceProfileArn    = $association.Arn
    associationState      = $association.State
    passwordParameterName = $PasswordParameterName
    passwordParameterArn  = $parameterArn
} | ConvertTo-Json -Depth 4
