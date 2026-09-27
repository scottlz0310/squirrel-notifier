# Pester v5 tests for Assert-RunbookResult.ps1
# リリース準備 PR の runbook-guard。結果の有無・合否・証跡・テスト後の変更・base の取り込みの判定を固定する。

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'Assert-RunbookResult.ps1'
    $script:Tested = 'a' * 40

    # git を fake にする。show は sha ごとの csproj、diff は変更ファイル、merge-base は終了コードを返す
    function global:git {
        $global:LASTEXITCODE = 0
        switch ($args[0]) {
            'show' {
                $sha = ($args[1] -split ':')[0]
                "<Project><PropertyGroup><Version>$($global:FakeVersions[$sha])</Version></PropertyGroup></Project>"
            }
            'diff' { $global:FakeChanged }
            'merge-base' { $global:LASTEXITCODE = $global:FakeMergeBaseExitCode }
        }
    }

    function script:New-Scenario([string]$Id, [bool]$Required = $true) {
        $content = "---`nid: $Id`nrequired: $($Required.ToString().ToLowerInvariant())`n---`n# $Id`n"
        Set-Content -LiteralPath (Join-Path $script:Root "tests/e2e/agent-runbooks/scenarios/$Id.md") -Value $content
    }

    function script:New-Entry([string]$Id, [string]$Result = 'pass', [string[]]$Kinds = @('uia')) {
        [ordered]@{
            id = $Id
            result = $Result
            evidence = @($Kinds | ForEach-Object { @{ kind = $_; path = 'evidence/x'; summary = 'observed' } })
        }
    }

    function script:Set-Result([object[]]$Scenarios, [hashtable]$Override = @{}) {
        $result = [ordered]@{
            schemaVersion = 1
            version = '1.1.0'
            testedCommit = $script:Tested
            executor = @{ agent = 'claude-code'; implementerSession = $false }
            scenarios = $Scenarios
        }
        foreach ($key in $Override.Keys) { $result[$key] = $Override[$key] }
        $result | ConvertTo-Json -Depth 6 |
            Set-Content -LiteralPath (Join-Path $script:Root 'tests/e2e/agent-runbooks/results/v1.1.0.json')
    }

    function script:Invoke-Guard {
        & $script:ScriptPath -HeadSha 'head' -BaseSha 'base'
    }
}

AfterAll {
    Remove-Item -Path Function:\git -ErrorAction SilentlyContinue
}

Describe 'Assert-RunbookResult' {
    BeforeEach {
        $script:Root = Join-Path ([IO.Path]::GetTempPath()) "runbook-guard-$([guid]::NewGuid())"
        New-Item -ItemType Directory -Force -Path (Join-Path $script:Root 'tests/e2e/agent-runbooks/scenarios'), (Join-Path $script:Root 'tests/e2e/agent-runbooks/results') | Out-Null
        Push-Location $script:Root
        $global:FakeVersions = @{ head = '1.1.0'; base = '1.0.0' }
        $global:FakeChanged = @('tests/e2e/agent-runbooks/results/v1.1.0.json')
        $global:FakeMergeBaseExitCode = 0
        New-Scenario 'isolated-launch'
        New-Scenario 'review-event-flow'
        New-Scenario 'optional-extra' -Required $false
    }

    AfterEach {
        Pop-Location
        Remove-Item -LiteralPath $script:Root -Recurse -Force
    }

    It 'version が base と同じなら結果が無くても検査しない' {
        $global:FakeVersions = @{ head = '1.0.0'; base = '1.0.0' }

        { Invoke-Guard } | Should -Not -Throw
    }

    It '必須シナリオがすべて pass で証跡があれば通す（任意シナリオは無くてよい）' {
        Set-Result @((New-Entry 'isolated-launch'), (New-Entry 'review-event-flow' -Kinds @('screenshot', 'log')))

        { Invoke-Guard } | Should -Not -Throw
    }

    It '結果ファイルが無ければ失敗する' {
        { Invoke-Guard } | Should -Throw '*results/v1.1.0.json がありません*'
    }

    It '結果の項目が不正なら失敗する: <Case>' -ForEach @(
        @{ Case = 'schemaVersion'; Override = @{ schemaVersion = 2 }; Message = '*schemaVersion*' }
        @{ Case = 'version の不一致'; Override = @{ version = '1.0.9' }; Message = '*version（1.0.9）*' }
        @{ Case = '短い SHA'; Override = @{ testedCommit = 'abc1234' }; Message = '*40 桁*' }
        @{ Case = '実装セッションでの実行'; Override = @{ executor = @{ agent = 'claude-code'; implementerSession = $true } }; Message = '*別のセッション*' }
        @{ Case = 'executor の欠落'; Override = @{ executor = $null }; Message = '*別のセッション*' }
    ) {
        Set-Result @((New-Entry 'isolated-launch'), (New-Entry 'review-event-flow')) -Override $Override

        { Invoke-Guard } | Should -Throw $Message
    }

    It '必須シナリオの結果が基準を満たさなければ失敗する: <Case>' -ForEach @(
        @{ Case = '結果が無い'; Entries = { @(New-Entry 'isolated-launch') }; Message = '*review-event-flow の結果がありません*' }
        @{ Case = 'fail'; Entries = { @((New-Entry 'isolated-launch'), (New-Entry 'review-event-flow' -Result 'fail')) }; Message = '*review-event-flow が pass ではありません（fail）*' }
        @{ Case = 'スクリーンショットだけ'; Entries = { @((New-Entry 'isolated-launch'), (New-Entry 'review-event-flow' -Kinds @('screenshot'))) }; Message = '*スクリーンショット以外の証跡がありません*' }
        @{ Case = '証跡なし'; Entries = { @((New-Entry 'isolated-launch'), (New-Entry 'review-event-flow' -Kinds @())) }; Message = '*スクリーンショット以外の証跡がありません*' }
    ) {
        Set-Result (& $Entries)

        { Invoke-Guard } | Should -Throw $Message
    }

    It 'テスト後に results 以外が変更されていれば失敗する' {
        Set-Result @((New-Entry 'isolated-launch'), (New-Entry 'review-event-flow'))
        $global:FakeChanged = @('tests/e2e/agent-runbooks/results/v1.1.0.json', 'winui3/SquirrelNotifier.WinUI3/MainWindow.xaml.cs')

        { Invoke-Guard } | Should -Throw '*results/ 以外が変更されています*MainWindow.xaml.cs*'
    }

    It 'テストしたコミットが base を含まなければ失敗する' {
        Set-Result @((New-Entry 'isolated-launch'), (New-Entry 'review-event-flow'))
        $global:FakeMergeBaseExitCode = 1

        { Invoke-Guard } | Should -Throw '*base（base）を含みません*'
    }

    It '必須シナリオが 1 つも無ければ失敗する' {
        Get-ChildItem (Join-Path $script:Root 'tests/e2e/agent-runbooks/scenarios') | Remove-Item
        Set-Result @()

        { Invoke-Guard } | Should -Throw '*必須シナリオがありません*'
    }
}
