# Pester v5 tests for DesktopRunnerHost.psm1
# 自動ログオンと runner 起動タスクの契約を固定する。
# - registry へ平文パスワードを書かない（LSA secret へ格納する）
# - runner を service（session 0）として起動しない（#378）
# - bootstrap レポートへ資格情報を混入させない

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot 'DesktopRunnerHost.psm1') -Force

    $script:TaskNamespace = 'http://schemas.microsoft.com/windows/2004/02/mit/task'

    function Get-TaskNode
    {
        param(
            [string]$Xml,
            [string]$XPath
        )

        $document = [xml]$Xml
        $manager = New-Object System.Xml.XmlNamespaceManager($document.NameTable)
        $manager.AddNamespace('t', $script:TaskNamespace)
        return $document.SelectSingleNode($XPath, $manager)
    }
}

Describe 'New-DesktopRunnerWinlogonPlan' {
    BeforeAll {
        $script:Plan = New-DesktopRunnerWinlogonPlan -UserName 'Administrator' -DomainName 'EC2AMAZ-TEST'
    }

    It 'Winlogon キーを対象にする' {
        $script:Plan.Path | Should -Be 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
    }

    It '<Name> に <Expected> を設定する' -ForEach @(
        @{ Name = 'AutoAdminLogon'; Expected = '1' }
        @{ Name = 'DefaultUserName'; Expected = 'Administrator' }
        @{ Name = 'DefaultDomainName'; Expected = 'EC2AMAZ-TEST' }
    ) {
        $script:Plan.SetValues[$Name] | Should -Be $Expected
    }

    It 'registry へ平文パスワードを書かない' {
        $script:Plan.SetValues.Keys | Should -Not -Contain 'DefaultPassword'
    }

    It '<Name> を削除対象にする' -ForEach @(
        @{ Name = 'DefaultPassword' }
        @{ Name = 'AutoLogonCount' }
    ) {
        $script:Plan.RemoveValues | Should -Contain $Name
    }
}

Describe 'New-DesktopRunnerLogonTaskXml' {
    BeforeAll {
        $script:Xml = New-DesktopRunnerLogonTaskXml `
            -TaskUserId 'EC2AMAZ-TEST\Administrator' `
            -RunnerDirectory 'C:\Users\Administrator\actions-runner' `
            -StartDelaySeconds 45
    }

    It 'well-formed な XML を返す' {
        { [xml]$script:Xml } | Should -Not -Throw
    }

    It 'service ではなく対話トークンで起動する' {
        $node = Get-TaskNode -Xml $script:Xml -XPath '/t:Task/t:Principals/t:Principal/t:LogonType'
        $node.InnerText | Should -Be 'InteractiveToken'
    }

    It '昇格した権限で起動する' {
        $node = Get-TaskNode -Xml $script:Xml -XPath '/t:Task/t:Principals/t:Principal/t:RunLevel'
        $node.InnerText | Should -Be 'HighestAvailable'
    }

    It '<XPath> が <Expected> を指す' -ForEach @(
        @{ XPath = '/t:Task/t:Triggers/t:LogonTrigger/t:UserId'; Expected = 'EC2AMAZ-TEST\Administrator' }
        @{ XPath = '/t:Task/t:Principals/t:Principal/t:UserId'; Expected = 'EC2AMAZ-TEST\Administrator' }
        @{ XPath = '/t:Task/t:Triggers/t:LogonTrigger/t:Delay'; Expected = 'PT45S' }
        @{ XPath = '/t:Task/t:Actions/t:Exec/t:Command'; Expected = 'cmd.exe' }
        @{ XPath = '/t:Task/t:Actions/t:Exec/t:WorkingDirectory'; Expected = 'C:\Users\Administrator\actions-runner' }
    ) {
        $node = Get-TaskNode -Xml $script:Xml -XPath $XPath
        $node.InnerText | Should -Be $Expected
    }

    It 'runner が待機し続けても打ち切らない' {
        $node = Get-TaskNode -Xml $script:Xml -XPath '/t:Task/t:Settings/t:ExecutionTimeLimit'
        $node.InnerText | Should -Be 'PT0S'
    }

    It 'XML 特殊文字を含むパスをエスケープする' {
        $xml = New-DesktopRunnerLogonTaskXml -TaskUserId 'HOST\User' -RunnerDirectory 'C:\a&b\runner'
        { [xml]$xml } | Should -Not -Throw

        $node = Get-TaskNode -Xml $xml -XPath '/t:Task/t:Actions/t:Exec/t:WorkingDirectory'
        $node.InnerText | Should -Be 'C:\a&b\runner'
    }
}

Describe 'New-DesktopRunnerSessionPolicyPlan' {
    BeforeAll {
        $script:Policy = New-DesktopRunnerSessionPolicyPlan
    }

    It '画面ロックを無効化する' {
        $plan = $script:Policy.RegistryPlans | Where-Object { $_.Path -like '*\Personalization' }
        $plan.SetValues['NoLockScreen'] | Should -Be 1
    }

    It 'スクリーンセーバーを無効化する' {
        $plan = $script:Policy.RegistryPlans | Where-Object { $_.Path -like '*\Control Panel\Desktop' }
        $plan.SetValues['ScreenSaveActive'] | Should -Be '0'
    }

    It 'ユーザー個別ではなくマシンポリシーへ書く' {
        foreach ($plan in $script:Policy.RegistryPlans)
        {
            $plan.Path | Should -BeLike 'HKLM:\*'
        }
    }

    It '<Setting> を 0 にする' -ForEach @(
        @{ Setting = 'standby-timeout-ac' }
        @{ Setting = 'monitor-timeout-ac' }
        @{ Setting = 'disk-timeout-ac' }
        @{ Setting = 'hibernate-timeout-ac' }
    ) {
        $entry = $script:Policy.PowerSettings | Where-Object { $_.Setting -eq $Setting }
        $entry | Should -Not -BeNullOrEmpty
        $entry.Value | Should -Be '0'
    }
}

Describe 'New-DesktopRunnerBootstrapReport' {
    BeforeAll {
        $script:Report = New-DesktopRunnerBootstrapReport `
            -ComputerName 'EC2AMAZ-TEST' `
            -AutoLogonUserId 'EC2AMAZ-TEST\Administrator' `
            -RunnerDirectory 'C:\Users\Administrator\actions-runner' `
            -TaskName 'GitHubActionsRunner-SquirrelNotifierDesktop' `
            -CompletedSteps @('lsa-secret', 'winlogon-registry', 'logon-task', 'session-policy') `
            -PasswordSource 'ssm-parameter-store'
    }

    It '実施したステップを記録する' {
        $script:Report.completedSteps | Should -Contain 'lsa-secret'
        $script:Report.completedSteps | Should -Contain 'logon-task'
    }

    It 'パスワードの値ではなく出所だけを記録する' {
        $script:Report.passwordSource | Should -Be 'ssm-parameter-store'
    }

    It 'パスワードを受け取る引数を持たない' {
        $command = Get-Command New-DesktopRunnerBootstrapReport
        $command.Parameters.Keys | Should -Not -Contain 'Password'
        $command.Parameters.Keys | Should -Not -Contain 'PlainPassword'
    }

    It 'UTC の完了時刻を記録する' {
        $script:Report.completedAtUtc | Should -Match '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$'
    }
}

Describe 'SSM Run Command 互換のエンコーディング' {
    # Windows PowerShell 5.1（AWS-RunPowerShellScript の既定 shell）は BOM の無い UTF-8 を
    # ANSI として読むため、日本語コメントがあると構文エラーになる。BOM の欠落を回帰させない。
    It '<Name> は UTF-8 BOM で保存されている' -ForEach @(
        @{ Name = 'DesktopRunnerHost.psm1' }
        @{ Name = 'Setup-DesktopRunnerHost.ps1' }
        @{ Name = 'Initialize-DesktopRunnerInstanceProfile.ps1' }
        @{ Name = 'Invoke-DesktopRunnerBootstrap.ps1' }
    ) {
        $path = Join-Path $PSScriptRoot $Name
        $bytes = [System.IO.File]::ReadAllBytes($path)
        $bytes[0..2] | Should -Be @(0xEF, 0xBB, 0xBF)
    }
}
