# Pester v5 tests for Test-DesktopE2EOidcBoundary.ps1
# OIDC ロールの権限境界の確認を固定する（#380）。
# - 直接 RunInstances と永続 instance の terminate を --dry-run で拒否する
# - 期待どおりなら結果を返し、期待と違う・判定できない結果が 1 件でもあれば失敗させる
# aws CLI は、呼び出しを記録して操作ごとの DryRun の結果を返す関数で置き換える。

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'Test-DesktopE2EOidcBoundary.ps1'

    function global:aws
    {
        $global:FakeCalls.Add(($args -join ' '))

        $case = switch -Wildcard ($args -join ' ')
        {
            'ec2 terminate-instances*' { 'terminate-persistent-instance'; break }
            default { 'direct-run-instances' }
        }

        $global:LASTEXITCODE = 254
        return "An error occurred ($($global:FakeState[$case])) when calling the operation"
    }

    function Invoke-Boundary
    {
        & $script:ScriptPath -LaunchTemplateId 'lt-09e208553b742f6c1' -ReferenceInstanceId 'i-00b4e23b910eade6c'
    }
}

AfterAll {
    Remove-Item -Path 'function:global:aws' -ErrorAction SilentlyContinue
    Remove-Variable -Name 'FakeCalls', 'FakeState' -Scope Global -ErrorAction SilentlyContinue
}

Describe 'Test-DesktopE2EOidcBoundary.ps1' {
    BeforeEach {
        $global:FakeCalls = [System.Collections.Generic.List[string]]::new()
        $global:FakeState = @{
            'direct-run-instances'           = 'UnauthorizedOperation'
            'terminate-persistent-instance' = 'UnauthorizedOperation'
        }
    }

    It '期待どおりなら全件の結果を返す' {
        $report = Invoke-Boundary | ConvertFrom-Json

        @($report.results).Count | Should -Be 2
        @($report.results | Where-Object { $_.actual -ne $_.expected }) | Should -BeNullOrEmpty
    }

    It '期待どおりなら $LASTEXITCODE を 0 で終える' {
        # Actions の shell: pwsh はスクリプト末尾の $LASTEXITCODE を step の終了コードにする（run 35916746752）
        Invoke-Boundary | Out-Null

        $global:LASTEXITCODE | Should -Be 0
    }

    It 'すべて --dry-run と region 付きで呼ぶ' {
        Invoke-Boundary | Out-Null

        $dryRuns = @($global:FakeCalls | Where-Object { $_ -like '*--dry-run*' })
        $dryRuns.Count | Should -Be 2
        $dryRuns | ForEach-Object { $_ | Should -BeLike '*--region us-east-1*' }
        @($global:FakeCalls | Where-Object { $_ -notlike '*--dry-run*' }) | Should -BeNullOrEmpty
    }

    It '<Case> が <Code> なら失敗させる' -ForEach @(
        @{ Case = 'direct-run-instances'; Code = 'DryRunOperation' }
        @{ Case = 'terminate-persistent-instance'; Code = 'DryRunOperation' }
        @{ Case = 'direct-run-instances'; Code = 'InvalidParameterCombination' }
    ) {
        $global:FakeState[$Case] = $Code

        { Invoke-Boundary } | Should -Throw "*$Case*"
    }
}
