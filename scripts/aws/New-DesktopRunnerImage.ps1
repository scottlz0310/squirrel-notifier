<#
.SYNOPSIS
  desktop E2E runner の既存 instance から、使い捨て instance 用の AMI を作成する（#380）。
.DESCRIPTION
  停止中の instance だけを対象にする。起動中の instance から作ると、CreateImage が instance を
  再起動して実行中の job を壊すか、--no-reboot ではファイルシステムの整合性が保証されない。

  AMI と snapshot に Get-DesktopRunnerResourceTag のタグを付ける。OIDC ロールは、このタグの
  付いた自アカウントの AMI からだけ instance を起動できる。AMI の作成後は
  Initialize-DesktopRunnerLaunchTemplate.ps1 で Launch Template の default version を更新する。

  AMI には自動ログオンの資格情報（LSA secret）と永続 runner の資格情報が含まれる。AMI と snapshot を
  共有・公開してはならない。永続 runner の資格情報は、使い捨て instance の起動時に
  Start-DesktopRunner.ps1 が削除する。

  作成前に、instance へ main の bootstrap（Invoke-DesktopRunnerBootstrap.ps1）を適用しておく。

  Admin ロールで実行する（ec2:CreateImage と ec2:CreateTags が必要）。
.EXAMPLE
  $env:AWS_PROFILE = 'admin'
  .\New-DesktopRunnerImage.ps1 -SourceInstanceId i-0123456789abcdef0
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SourceInstanceId,

    [ValidateNotNullOrEmpty()]
    [string]$Region = 'us-east-1',

    [ValidateRange(5, 180)]
    [int]$TimeoutMinutes = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'DesktopRunnerImage.psm1') -Force

function Invoke-AwsCli
{
    <#
    .SYNOPSIS
      aws CLI を呼び出し、失敗を例外へ変換する。
    #>
    param([string[]]$Arguments)

    $output = & aws @Arguments 2>&1
    if ($LASTEXITCODE -ne 0)
    {
        throw "aws CLI が失敗しました: aws $($Arguments -join ' ')`n$output"
    }

    return ($output | Out-String).Trim()
}

$imageName = 'squirrel-notifier-desktop-e2e-runner-' + (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')

# 書き込みより前に組み立てる。入力値の検証もここで行われ、不正な値では何も作らずに止まる。
$tagSpecifications = New-DesktopRunnerImageTagSpecification -ImageName $imageName -SourceInstanceId $SourceInstanceId

$state = Invoke-AwsCli -Arguments @(
    'ec2', 'describe-instances',
    '--region', $Region,
    '--instance-ids', $SourceInstanceId,
    '--query', 'Reservations[0].Instances[0].State.Name',
    '--output', 'text'
)
if ($state -ne 'stopped')
{
    throw "instance $SourceInstanceId は '$state' です。停止してから実行してください（起動中の instance からは整合した AMI を作れません）。"
}

if (-not $PSCmdlet.ShouldProcess($SourceInstanceId, "AMI $imageName を作成"))
{
    return
}

$tagSpecificationsPath = Join-Path ([System.IO.Path]::GetTempPath()) ("desktop-runner-image-" + [guid]::NewGuid().ToString('N') + '.json')
Set-Content -LiteralPath $tagSpecificationsPath -Value (ConvertTo-Json -InputObject $tagSpecifications -Depth 10) -Encoding ascii
try
{
    $imageId = Invoke-AwsCli -Arguments @(
        'ec2', 'create-image',
        '--region', $Region,
        '--instance-id', $SourceInstanceId,
        '--name', $imageName,
        '--description', "squirrel-notifier desktop E2E runner image from $SourceInstanceId (#380)",
        '--tag-specifications', "file://$tagSpecificationsPath",
        '--query', 'ImageId',
        '--output', 'text'
    )
}
finally
{
    Remove-Item -LiteralPath $tagSpecificationsPath -Force -ErrorAction SilentlyContinue
}

Write-Host "AMI $imageId を作成中です。available になるまで待機します（最大 $TimeoutMinutes 分）。"

# aws ec2 wait image-available は約 10 分で打ち切られるため、上限を指定できるよう自前で待つ。
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
$imageState = ''
while ((Get-Date) -lt $deadline)
{
    $imageState = Invoke-AwsCli -Arguments @(
        'ec2', 'describe-images',
        '--region', $Region,
        '--image-ids', $imageId,
        '--query', 'Images[0].State',
        '--output', 'text'
    )

    if ($imageState -eq 'available')
    {
        break
    }

    if ($imageState -in @('failed', 'error', 'invalid', 'deregistered'))
    {
        throw "AMI $imageId の作成に失敗しました: $imageState"
    }

    Start-Sleep -Seconds 30
}

if ($imageState -ne 'available')
{
    throw "AMI $imageId が $TimeoutMinutes 分以内に available になりませんでした（最後の状態: '$imageState'）。作成は継続している可能性があるため、状態を確認してから Launch Template を更新してください。"
}

[pscustomobject]@{
    schemaVersion    = 1
    region           = $Region
    imageId          = $imageId
    imageName        = $imageName
    sourceInstanceId = $SourceInstanceId
    state            = $imageState
} | ConvertTo-Json -Depth 4
