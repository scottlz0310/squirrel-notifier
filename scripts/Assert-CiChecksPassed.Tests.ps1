# Pester v5 tests for Assert-CiChecksPassed.ps1
# release の publish 前ゲート。必須ジョブの成否判定・再実行の扱い・他 App の除外・待機を固定する。

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'Assert-CiChecksPassed.ps1'

    # gh を fake にする。呼び出しごとに $global:FakeGhResponses を 1 つずつ返す
    function global:gh {
        $global:FakeGhCalls++
        $global:LASTEXITCODE = $global:FakeGhExitCode
        $index = [Math]::Min($global:FakeGhCalls, $global:FakeGhResponses.Count) - 1
        $global:FakeGhResponses[$index] | ForEach-Object { $_ | ConvertTo-Json -Compress }
    }

    function script:New-Run([long]$Id, [string]$Name, [string]$Status = 'completed', [string]$Conclusion = 'success', [string]$App = 'github-actions') {
        [pscustomobject]@{ id = $Id; name = $Name; status = $Status; conclusion = $Conclusion; app = $App }
    }

    function script:Invoke-Gate {
        & $script:ScriptPath -Repository 'owner/repo' -Sha 'abc' -RequiredChecks @('build-and-test', 'headless-e2e') -TimeoutMinutes 0 -PollSeconds 0
    }
}

AfterAll {
    Remove-Item -Path Function:\gh -ErrorAction SilentlyContinue
}

Describe 'Assert-CiChecksPassed' {
    BeforeEach {
        $global:FakeGhCalls = 0
        $global:FakeGhExitCode = 0
    }

    It '必須ジョブがすべて成功していれば通す' {
        $global:FakeGhResponses = @(, @((New-Run 1 'build-and-test'), (New-Run 2 'headless-e2e'), (New-Run 3 'other' -Conclusion 'failure')))

        { Invoke-Gate } | Should -Not -Throw
    }

    It '完了した必須ジョブが <Conclusion> なら待たずに失敗する' -ForEach @(
        @{ Conclusion = 'failure' }
        @{ Conclusion = 'cancelled' }
        @{ Conclusion = 'skipped' }
    ) {
        $global:FakeGhResponses = @(, @((New-Run 1 'build-and-test'), (New-Run 2 'headless-e2e' -Conclusion $Conclusion)))

        { Invoke-Gate } | Should -Throw "*headless-e2e ($Conclusion)*"
        $global:FakeGhCalls | Should -Be 1
    }

    It '必須ジョブが無い、または実行中のまま期限を過ぎたら失敗する' {
        $global:FakeGhResponses = @(, @((New-Run 1 'build-and-test' -Status 'in_progress' -Conclusion $null)))

        { Invoke-Gate } | Should -Throw '*未完了のジョブがあります*build-and-test, headless-e2e*'
    }

    It '他 App が作った同名の check run は採用しない' {
        $global:FakeGhResponses = @(, @((New-Run 1 'build-and-test' -App 'evil-app'), (New-Run 2 'headless-e2e')))

        { Invoke-Gate } | Should -Throw '*未完了のジョブがあります*build-and-test*'
    }

    It '再実行は id が最大の run で判定する' {
        $global:FakeGhResponses = @(, @(
                (New-Run 5 'build-and-test'),
                (New-Run 4 'build-and-test' -Conclusion 'failure'),
                (New-Run 2 'headless-e2e')))

        { Invoke-Gate } | Should -Not -Throw
    }

    It '実行中の間は待機し、完了後に判定する' {
        $global:FakeGhResponses = @(
            @((New-Run 1 'build-and-test' -Status 'in_progress' -Conclusion $null), (New-Run 2 'headless-e2e')),
            @((New-Run 1 'build-and-test'), (New-Run 2 'headless-e2e')))

        & $script:ScriptPath -Repository 'owner/repo' -Sha 'abc' -RequiredChecks @('build-and-test', 'headless-e2e') -TimeoutMinutes 1 -PollSeconds 0

        $global:FakeGhCalls | Should -Be 2
    }

    It 'check run を取得できなければ失敗する' {
        $global:FakeGhResponses = @(, @())
        $global:FakeGhExitCode = 1

        { Invoke-Gate } | Should -Throw '*check run を取得できませんでした: owner/repo@abc*'
    }
}
