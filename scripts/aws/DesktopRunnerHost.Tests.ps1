# Pester v5 tests for DesktopRunnerHost.psm1
# 自動ログオンと runner 起動タスクの契約を固定する。
# - registry へ平文パスワードを書かない（LSA secret へ格納する）
# - runner を service（session 0）として起動しない（#378）
# - bootstrap レポートへ資格情報を混入させない
# - AMI から起動した instance で永続 runner の資格情報を使わない（#380）

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
            -LauncherPath 'C:\ProgramData\SquirrelNotifier\desktop-runner\Start-DesktopRunner.ps1' `
            -ConfigPath 'C:\ProgramData\SquirrelNotifier\desktop-runner\launcher.json' `
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
        @{ XPath = '/t:Task/t:Actions/t:Exec/t:Command'; Expected = 'powershell.exe' }
        @{ XPath = '/t:Task/t:Actions/t:Exec/t:Arguments'; Expected = '-NoProfile -ExecutionPolicy Bypass -File "C:\ProgramData\SquirrelNotifier\desktop-runner\Start-DesktopRunner.ps1" -ConfigPath "C:\ProgramData\SquirrelNotifier\desktop-runner\launcher.json"' }
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
        $xml = New-DesktopRunnerLogonTaskXml `
            -TaskUserId 'HOST\User' `
            -RunnerDirectory 'C:\a&b\runner' `
            -LauncherPath 'C:\a&b\Start-DesktopRunner.ps1' `
            -ConfigPath 'C:\a&b\launcher.json'
        { [xml]$xml } | Should -Not -Throw

        $node = Get-TaskNode -Xml $xml -XPath '/t:Task/t:Actions/t:Exec/t:WorkingDirectory'
        $node.InnerText | Should -Be 'C:\a&b\runner'

        $node = Get-TaskNode -Xml $xml -XPath '/t:Task/t:Actions/t:Exec/t:Arguments'
        $node.InnerText | Should -BeLike '*-File "C:\a&b\Start-DesktopRunner.ps1"*'
    }
}

Describe 'New-DesktopRunnerLaunchPlan' {
    BeforeAll {
        $script:LaunchArguments = @{
            LegacyInstanceId   = 'i-00b4e23b910eade6c'
            RunnerDirectory    = 'C:\Users\Administrator\actions-runner'
            JitParameterPrefix = '/squirrel-notifier/desktop-e2e/jit'
        }
    }

    It '<Case> は <Expected> で起動する' -ForEach @(
        @{ Case = 'bootstrap を適用した instance'; CurrentInstanceId = 'i-00b4e23b910eade6c'; Expected = 'persistent' }
        @{ Case = 'AMI から起動した instance'; CurrentInstanceId = 'i-0123456789abcdef0'; Expected = 'jit' }
    ) {
        $plan = New-DesktopRunnerLaunchPlan -CurrentInstanceId $CurrentInstanceId @script:LaunchArguments
        $plan.Mode | Should -Be $Expected
    }

    It '永続 runner の instance では資格情報を削除しない' {
        $plan = New-DesktopRunnerLaunchPlan -CurrentInstanceId 'i-00b4e23b910eade6c' @script:LaunchArguments
        $plan.CredentialFilesToRemove | Should -BeNullOrEmpty
        $plan.JitParameterName | Should -BeNullOrEmpty
    }

    It 'JIT runner の instance では永続 runner の <Name> を削除する' -ForEach @(
        @{ Name = '.runner' }
        @{ Name = '.credentials' }
        @{ Name = '.credentials_rsaparams' }
    ) {
        $plan = New-DesktopRunnerLaunchPlan -CurrentInstanceId 'i-0123456789abcdef0' @script:LaunchArguments
        $plan.CredentialFilesToRemove | Should -Contain ('C:\Users\Administrator\actions-runner\' + $Name)
    }

    It '末尾に区切り文字がある runner ディレクトリでも区切り文字を重ねない' {
        $arguments = @{} + $script:LaunchArguments
        $arguments['RunnerDirectory'] = 'C:\Users\Administrator\actions-runner\'
        $plan = New-DesktopRunnerLaunchPlan -CurrentInstanceId 'i-0123456789abcdef0' @arguments
        $plan.CredentialFilesToRemove | Should -Contain 'C:\Users\Administrator\actions-runner\.runner'
    }

    It 'JIT config の parameter 名を instance ID から決める' {
        $plan = New-DesktopRunnerLaunchPlan -CurrentInstanceId 'i-0123456789abcdef0' @script:LaunchArguments
        $plan.JitParameterName | Should -Be '/squirrel-notifier/desktop-e2e/jit/i-0123456789abcdef0'
    }

    It '<Name> が不正な値を拒否する' -ForEach @(
        @{ Name = 'CurrentInstanceId'; Override = @{ CurrentInstanceId = 'not-an-instance' } }
        @{ Name = 'LegacyInstanceId'; Override = @{ CurrentInstanceId = 'i-0123456789abcdef0'; LegacyInstanceId = 'None' } }
        @{ Name = 'JitParameterPrefix'; Override = @{ CurrentInstanceId = 'i-0123456789abcdef0'; JitParameterPrefix = 'squirrel/jit/' } }
    ) {
        $invalidArguments = @{} + $script:LaunchArguments
        foreach ($key in $Override.Keys)
        {
            $invalidArguments[$key] = $Override[$key]
        }

        { New-DesktopRunnerLaunchPlan @invalidArguments } | Should -Throw
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

Describe 'Get-DesktopRunnerInstanceProfileDecision' {
    # 「何かが関連付けられている」ことを関連付け済みと扱うと、別 profile が付いた instance を
    # 成功扱いにし、SSM role が無いまま Run Command が権限不足で失敗する。
    It '<Case> は <Expected> を返す' -ForEach @(
        @{ Case = '未関連付け'; Arn = ''; State = ''; Expected = 'associate' }
        @{ Case = 'aws cli の None'; Arn = 'None'; State = 'None'; Expected = 'associate' }
        @{ Case = '期待する profile が associated'; Arn = 'arn:aws:iam::1:instance-profile/Expected'; State = 'associated'; Expected = 'ok' }
        @{ Case = '期待する profile が associating'; Arn = 'arn:aws:iam::1:instance-profile/Expected'; State = 'associating'; Expected = 'wait' }
        @{ Case = '別 profile が associated'; Arn = 'arn:aws:iam::1:instance-profile/Other'; State = 'associated'; Expected = 'conflict' }
        @{ Case = '別 profile が associating'; Arn = 'arn:aws:iam::1:instance-profile/Other'; State = 'associating'; Expected = 'conflict' }
    ) {
        $decision = Get-DesktopRunnerInstanceProfileDecision -CurrentArn $Arn -CurrentState $State -ExpectedName 'Expected'
        $decision.Action | Should -Be $Expected
    }

    It '衝突時に現在の profile 名と ARN を報告する' {
        $decision = Get-DesktopRunnerInstanceProfileDecision `
            -CurrentArn 'arn:aws:iam::1:instance-profile/Other' `
            -CurrentState 'associated' `
            -ExpectedName 'Expected'

        $decision.CurrentName | Should -Be 'Other'
        $decision.Reason | Should -BeLike '*arn:aws:iam::1:instance-profile/Other*'
    }

    It 'associated 以外を完了と判定しない' {
        $decision = Get-DesktopRunnerInstanceProfileDecision `
            -CurrentArn 'arn:aws:iam::1:instance-profile/Expected' `
            -CurrentState 'associating' `
            -ExpectedName 'Expected'

        $decision.Action | Should -Not -Be 'ok'
    }
}

Describe 'SSM Run Command 互換のエンコーディング' {
    # Windows PowerShell 5.1（AWS-RunPowerShellScript の既定 shell）は BOM の無い UTF-8 を
    # ANSI として読むため、日本語コメントがあると構文エラーになる。BOM の欠落を回帰させない。
    It '<Name> は UTF-8 BOM で保存されている' -ForEach @(
        @{ Name = 'DesktopRunnerHost.psm1' }
        @{ Name = 'Setup-DesktopRunnerHost.ps1' }
        @{ Name = 'Start-DesktopRunner.ps1' }
        @{ Name = 'Initialize-DesktopRunnerInstanceProfile.ps1' }
        @{ Name = 'Invoke-DesktopRunnerBootstrap.ps1' }
        @{ Name = 'Set-DesktopRunnerAutoLogonPassword.ps1' }
    ) {
        $path = Join-Path $PSScriptRoot $Name
        $bytes = [System.IO.File]::ReadAllBytes($path)
        $bytes[0..2] | Should -Be @(0xEF, 0xBB, 0xBF)
    }
}
