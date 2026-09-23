BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'Resolve-DesktopE2EAutomationInstance.ps1'
    function global:Start-Sleep { }
    function global:aws
    {
        $global:LASTEXITCODE = 0
        $status = if ($global:FakeStatuses.Count -gt 1) { $global:FakeStatuses.Dequeue() } else { $global:FakeStatuses.Peek() }
        $outputs = if ($status -eq 'Success') { @{ 'launchInstance.InstanceId' = @('i-0aaaaaaaaaaaaaaaa') } } else { @{} }
        return (@{ AutomationExecution = @{ AutomationExecutionStatus = $status; Outputs = $outputs } } | ConvertTo-Json -Depth 5)
    }
}

AfterAll {
    Remove-Item -Path 'function:global:aws', 'function:global:Start-Sleep' -ErrorAction SilentlyContinue
    Remove-Variable -Name 'FakeStatuses' -Scope Global -ErrorAction SilentlyContinue
}

Describe 'Resolve-DesktopE2EAutomationInstance.ps1' {
    BeforeEach {
        $global:FakeStatuses = [System.Collections.Generic.Queue[string]]::new()
    }

    It 'Automation 完了後の instance ID を復元する' {
        $global:FakeStatuses.Enqueue('InProgress')
        $global:FakeStatuses.Enqueue('Success')

        & $script:ScriptPath -AutomationExecutionId '11111111-2222-3333-4444-555555555555' |
            Should -Be 'i-0aaaaaaaaaaaaaaaa'
    }

    It '失敗して instance ID が無い場合は原因を返す' {
        $global:FakeStatuses.Enqueue('Failed')

        { & $script:ScriptPath -AutomationExecutionId '11111111-2222-3333-4444-555555555555' } |
            Should -Throw '*Failed*instance ID*'
    }
}
