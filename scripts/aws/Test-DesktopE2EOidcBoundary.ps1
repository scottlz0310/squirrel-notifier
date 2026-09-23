<#
.SYNOPSIS
  desktop E2E workflow の OIDC ロールの権限境界を DryRun で確かめる（#380）。
.DESCRIPTION
  使い捨て runner を起動する前に、workflow の資格情報（OIDC ロール）で次を確かめる。

  - Launch Template の default version のままの RunInstances は許可される
  - network interface（subnet / Security Group）・block device・instance type を要求で上書きする
    RunInstances は拒否される
  - ephemeral-runner タグの無い instance（永続 instance）の TerminateInstances は拒否される

  ポリシー文書の形は DesktopE2EOidcRole.psm1 の Pester で固定しているが、AWS 上の実際の評価は
  ここでしか確かめられない。すべて --dry-run のため instance は作られず、terminate もされない。

  期待と違う結果、または認可の判定にならなかった結果（入力の誤り・通信の失敗など）が 1 件でもあれば
  失敗させる。ポリシーが後から変わった場合にも、使い捨て instance を作る前に気付けるようにする。

  上書きの値は永続 instance の subnet と Security Group から取る（OIDC ロールは Describe 系を
  instance にしか持たないため）。
.EXAMPLE
  .\Test-DesktopE2EOidcBoundary.ps1 -LaunchTemplateId lt-09e208553b742f6c1 -ReferenceInstanceId i-00b4e23b910eade6c
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^lt-[0-9a-f]{8,17}$')]
    [string]$LaunchTemplateId,

    [Parameter(Mandatory)]
    [ValidatePattern('^i-[0-9a-f]{8,17}$')]
    [string]$ReferenceInstanceId,

    [ValidateNotNullOrEmpty()]
    [string]$Region = 'us-east-1'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'DesktopEphemeralRunner.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'DesktopE2ECli.psm1') -Force

$reference = Invoke-AwsCli -Arguments @(
    'ec2', 'describe-instances',
    '--region', $Region,
    '--instance-ids', $ReferenceInstanceId,
    '--query', 'Reservations[0].Instances[0].{SubnetId: SubnetId, SecurityGroupId: SecurityGroups[0].GroupId}',
    '--output', 'json'
) | ConvertFrom-Json

$cases = Get-DesktopE2EOidcBoundaryCase `
    -LaunchTemplateId $LaunchTemplateId `
    -ReferenceInstanceId $ReferenceInstanceId `
    -ReferenceSubnetId $reference.SubnetId `
    -ReferenceSecurityGroupId $reference.SecurityGroupId

$results = @(
    foreach ($case in $cases)
    {
        $response = Invoke-AwsDryRun -Arguments ($case.Arguments + @('--region', $Region))
        $actual = Resolve-DesktopE2EDryRunOutcome -ExitCode $response.ExitCode -Output $response.Output
        [pscustomobject]@{
            name     = $case.Name
            expected = $case.Expected
            actual   = $actual
            # unknown のときだけ原因を残す。allowed / denied の出力はエラーコードの定型文で情報が無い。
            detail   = if ($actual -eq 'unknown') { $response.Output } else { $null }
        }
    }
)

$report = [pscustomobject]@{
    schemaVersion    = 1
    launchTemplateId = $LaunchTemplateId
    referenceId      = $ReferenceInstanceId
    results          = $results
} | ConvertTo-Json -Depth 4
$report

$mismatches = @($results | Where-Object { $_.actual -ne $_.expected })
if ($mismatches.Count -gt 0)
{
    $summary = ($mismatches | ForEach-Object { "$($_.name)（期待 $($_.expected) / 実際 $($_.actual)）" }) -join ', '
    throw "OIDC ロールの権限境界が期待と一致しません: $summary。使い捨て instance は起動していません。"
}
