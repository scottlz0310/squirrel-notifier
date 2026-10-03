BeforeAll {
    . (Join-Path $PSScriptRoot 'Get-CodeBehindSizeFindings.ps1')
}

Describe 'code-behind の行数検出' {
    BeforeEach {
        $script:Root = Join-Path $TestDrive ([guid]::NewGuid().ToString())
        $script:Relative = 'winui3/App/MainWindow.xaml.cs'
        $script:Path = Join-Path $script:Root $script:Relative
        New-Item -ItemType Directory -Path (Split-Path $script:Path) -Force | Out-Null
        $script:Limits = [ordered]@{ $script:Relative = 3 }
    }

    It '上限との境界を判定する' -ForEach @(
        @{ Lines = @('a', 'b'); Count = 2; Violations = 0 }
        @{ Lines = @('a', '', 'b'); Count = 3; Violations = 0 }
        @{ Lines = @('a', '', '', 'b'); Count = 4; Violations = 1 }
        @{ Lines = @(); Count = 0; Violations = 0 }
    ) {
        [IO.File]::WriteAllText($script:Path, ($Lines -join "`n"))
        $result = Get-CodeBehindSizeFindings -RepositoryRoot $script:Root -Limits $script:Limits
        $result.Measurements.Count | Should -Be 1
        $result.Measurements[0].Actual | Should -Be $Count
        $result.Violations.Count | Should -Be $Violations
        $result.Missing.Count | Should -Be 0
        if ($Violations) {
            $result.Violations[0] | Should -BeLike '*4 行（上限 3 行、超過 1 行）*'
        }
    }

    It '登録済みファイルの欠落を検出する' {
        $result = Get-CodeBehindSizeFindings -RepositoryRoot $script:Root -Limits $script:Limits
        $result.Violations.Count | Should -Be 1
        $result.Violations[0] | Should -BeLike '*ファイルが存在しません*'
        $result.Measurements.Count | Should -Be 0
    }

    It '未登録ファイルを検出するが生成物と他の拡張子は除外する' {
        Set-Content -LiteralPath $script:Path -Value 'a'
        foreach ($relative in @('winui3/App/New.xaml.cs', 'winui3/App/obj/Generated.xaml.cs', 'winui3/App/bin/Generated.xaml.cs', 'winui3/App/Service.cs')) {
            $path = Join-Path $script:Root $relative
            New-Item -ItemType Directory -Path (Split-Path $path) -Force | Out-Null
            Set-Content -LiteralPath $path -Value 'a'
        }
        $result = Get-CodeBehindSizeFindings -RepositoryRoot $script:Root -Limits $script:Limits
        $result.Missing.Count | Should -Be 1
        $result.Missing[0] | Should -Be 'winui3/App/New.xaml.cs'
        $result.Violations.Count | Should -Be 0
    }

    It '登録パスとの照合では大文字小文字を区別しない' {
        Set-Content -LiteralPath $script:Path -Value 'a'
        Mock Get-ChildItem { [pscustomobject]@{ FullName = Join-Path $script:Root 'winui3/App/MAINWINDOW.xaml.cs' } }
        $result = Get-CodeBehindSizeFindings -RepositoryRoot $script:Root -Limits $script:Limits
        $result.Missing.Count | Should -Be 0
        $result.Violations.Count | Should -Be 0
    }
}
