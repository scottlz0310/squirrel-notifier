<#
.SYNOPSIS
  prepare-runner が失敗した後、SSM Automation の ID から使い捨て instance ID を復元する（#380）。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f-]{36}$')]
    [string]$AutomationExecutionId,

    [string]$Region = 'us-east-1'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'DesktopE2ECli.psm1') -Force

for ($attempt = 1; $attempt -le 36; $attempt++)
{
    $execution = Invoke-AwsCli -Arguments @(
        'ssm', 'get-automation-execution', '--region', $Region,
        '--automation-execution-id', $AutomationExecutionId, '--output', 'json'
    ) | ConvertFrom-Json
    $outputs = $execution.AutomationExecution.Outputs
    $ids = @()
    if ($null -ne $outputs -and $null -ne $outputs.PSObject.Properties['launchInstance.InstanceId'])
    {
        $ids = @($outputs.'launchInstance.InstanceId')
    }
    if ($ids.Count -eq 1 -and $ids[0] -cmatch '^i-[0-9a-f]{8,17}$')
    {
        $ids[0]
        return
    }
    if ($execution.AutomationExecution.AutomationExecutionStatus -notin @('Pending', 'InProgress', 'Waiting'))
    {
        throw "Automation $AutomationExecutionId は $($execution.AutomationExecution.AutomationExecutionStatus) で終了し、instance ID を返しませんでした。"
    }
    Start-Sleep -Seconds 5
}

throw "Automation $AutomationExecutionId の instance ID を制限時間内に取得できませんでした。"
