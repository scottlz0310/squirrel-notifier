BeforeAll {
    $script:GuardPath = Join-Path $PSScriptRoot 'Assert-RunbookResult.ps1'
    $script:GitPath = (Get-Command git -CommandType Application | Select-Object -First 1).Source
    function Invoke-FixtureGit {
        param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
        $output = & $script:GitPath @Arguments 2>&1
        if ($LASTEXITCODE -ne 0) { throw "fixture git が失敗しました: $($Arguments -join ' ') / $output" }
        $output
    }
}

Describe '準備ブランチ削除後のランブック再検証' {
    It 'Squash merge 後の新規 clone は SHA の明示 fetch で検証する: <Fetch>' -ForEach @(
        @{ Fetch = $false; Missing = $false }
        @{ Fetch = $true; Missing = $false }
        @{ Fetch = $true; Missing = $true }
    ) {
        $root = Join-Path $TestDrive ([guid]::NewGuid().ToString())
        $origin = Join-Path $root 'origin.git'
        $prepare = Join-Path $root 'prepare'
        $publish = Join-Path $root 'publish'
        New-Item -ItemType Directory -Path $root, $prepare | Out-Null
        Invoke-FixtureGit init --bare $origin | Out-Null
        Push-Location $prepare
        try {
            Invoke-FixtureGit init -b main | Out-Null
            Invoke-FixtureGit config user.email fixture@example.invalid | Out-Null
            Invoke-FixtureGit config user.name fixture | Out-Null
            Invoke-FixtureGit config core.hooksPath (Join-Path $root 'no-hooks') | Out-Null
            Invoke-FixtureGit remote add origin $origin | Out-Null
            $runbook = 'tests/e2e/agent-runbooks'
            New-Item -ItemType Directory -Path "$runbook/scenarios", "$runbook/results" -Force | Out-Null
            Set-Content project.csproj '<Project><Version>1.0.0</Version></Project>'
            Set-Content "$runbook/scenarios/fixture.md" "---`nid: fixture`nrequired: true`n---"
            Invoke-FixtureGit add . | Out-Null
            Invoke-FixtureGit commit -m 'chore: fixture の初期状態' | Out-Null
            $base = Invoke-FixtureGit rev-parse HEAD
            Invoke-FixtureGit switch -c release-test | Out-Null
            Set-Content project.csproj '<Project><Version>1.1.0</Version></Project>'
            Invoke-FixtureGit add . | Out-Null
            Invoke-FixtureGit commit -m 'chore(release): v1.1.0' | Out-Null
            $tested = Invoke-FixtureGit rev-parse HEAD
            @{
                schemaVersion = 1; version = '1.1.0'; testedCommit = $(if ($Missing) { '0' * 40 } else { $tested })
                executor = @{ implementerSession = $false }
                scenarios = @(@{ id = 'fixture'; result = 'pass'; evidence = @(@{ kind = 'log'; summary = '隔離 fixture の成功' }) })
            } | ConvertTo-Json -Depth 6 | Set-Content "$runbook/results/v1.1.0.json"
            Invoke-FixtureGit add . | Out-Null
            Invoke-FixtureGit commit -m 'test: ランブック結果だけを追加' | Out-Null
            Invoke-FixtureGit push origin release-test | Out-Null
            # GitHub の PR ref と同じく、削除する準備 branch とは別にコミットを保持する。
            Invoke-FixtureGit push origin 'HEAD:refs/pull/1/head' | Out-Null
            Invoke-FixtureGit switch main | Out-Null
            Invoke-FixtureGit merge --squash release-test | Out-Null
            Invoke-FixtureGit commit -m 'chore(release): v1.1.0' | Out-Null
            $head = Invoke-FixtureGit rev-parse HEAD
            Invoke-FixtureGit push origin main | Out-Null
            Invoke-FixtureGit push origin --delete release-test | Out-Null
        }
        finally { Pop-Location }

        Invoke-FixtureGit clone --no-local --branch main $origin $publish | Out-Null
        Push-Location $publish
        try {
            & $script:GitPath cat-file -e "$tested^{commit}" 2>$null
            $LASTEXITCODE | Should -Not -Be 0
            $assertion = { & $script:GuardPath -HeadSha $head -BaseSha $base -CsprojPath project.csproj -FetchTestedCommit:$Fetch }
            if ($Missing) { $assertion | Should -Throw '*git fetch*失敗*' }
            elseif ($Fetch) { $assertion | Should -Not -Throw }
            else { $assertion | Should -Throw '*git diff*失敗*' }
        }
        finally { Pop-Location }
    }
}
