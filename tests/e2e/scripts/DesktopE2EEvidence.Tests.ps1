# Pester v5 tests for DesktopE2EEvidence.psm1
# desktop E2E の version 照合と、失敗時に artifact へ残す証跡を固定する（#397）。

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot 'DesktopE2EEvidence.psm1') -Force
}

Describe 'ConvertTo-ReleaseVersion' {
    It '<Value> を <Expected> へ正規化する' -ForEach @(
        @{ Value = '0.14.0'; Expected = '0.14.0' }
        @{ Value = 'v0.14.0'; Expected = '0.14.0' }
        @{ Value = '0.14.0.0'; Expected = '0.14.0' }
        @{ Value = '0.14.0+49f1116c7ee95785b6dbb159c8e91bf6a8f5b20e'; Expected = '0.14.0' }
        @{ Value = '0.14.0-preview.1'; Expected = '0.14.0' }
        @{ Value = ' 0.14.0 '; Expected = '0.14.0' }
    ) {
        ConvertTo-ReleaseVersion -Value $Value | Should -BeExactly $Expected
    }

    It '<Value> は version として扱わない' -ForEach @(
        @{ Value = '' }
        @{ Value = '0.14' }
        @{ Value = '0.14.0.1' }
        @{ Value = 'SquirrelNotifier 0.14.0' }
    ) {
        ConvertTo-ReleaseVersion -Value $Value | Should -BeNullOrEmpty
    }
}

Describe 'Test-InstalledVersion' {
    It 'ProductVersion <ProductVersion> は期待値 0.14.0 と一致する' -ForEach @(
        @{ ProductVersion = '0.14.0' }
        @{ ProductVersion = '0.14.0+49f1116c7ee95785b6dbb159c8e91bf6a8f5b20e' }
    ) {
        $result = Test-InstalledVersion -ExpectedVersion '0.14.0' -ProductVersion $ProductVersion -FileVersion '0.14.0.0' -ExecutablePath 'C:\app\SquirrelNotifier.WinUI3.exe'

        $result.Matches | Should -BeTrue
        $result.Message | Should -BeNullOrEmpty
    }

    It '不一致時は実測値と実行ファイルをメッセージに含める: <Case>' -ForEach @(
        @{ Case = '別 version'; ProductVersion = '0.13.1+abc'; FileVersion = '0.13.1.0'; ExpectedProduct = 'ProductVersion=0.13.1+abc'; ExpectedFile = 'FileVersion=0.13.1.0' }
        @{ Case = '解釈不能'; ProductVersion = '1.0'; FileVersion = '1.0.0.0'; ExpectedProduct = 'ProductVersion=1.0'; ExpectedFile = 'FileVersion=1.0.0.0' }
        @{ Case = '欠落'; ProductVersion = $null; FileVersion = ''; ExpectedProduct = 'ProductVersion=(なし)'; ExpectedFile = 'FileVersion=(なし)' }
    ) {
        $result = Test-InstalledVersion -ExpectedVersion '0.14.0' -ProductVersion $ProductVersion -FileVersion $FileVersion -ExecutablePath 'C:\app\SquirrelNotifier.WinUI3.exe'

        $result.Matches | Should -BeFalse
        $result.Message | Should -Match ([regex]::Escape('期待値=0.14.0'))
        $result.Message | Should -Match ([regex]::Escape($ExpectedProduct))
        $result.Message | Should -Match ([regex]::Escape($ExpectedFile))
        $result.Message | Should -Match ([regex]::Escape('実行ファイル=C:\app\SquirrelNotifier.WinUI3.exe'))
    }
}

Describe 'Save-MsiLogArtifact' {
    BeforeEach {
        # TestDrive は Describe 内で共有されるため、テストごとに独立したディレクトリを使う
        $caseRoot = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $script:RunRoot = Join-Path $caseRoot 'run'
        $script:ArtifactDirectory = Join-Path $caseRoot 'artifact'
        New-Item -ItemType Directory -Path $script:RunRoot, $script:ArtifactDirectory -Force | Out-Null
        $script:Sanitize = { param($Text) $Text -replace '\{[0-9A-F-]{36}\}', '<redacted>' }
    }

    It '<Encoding> のログをサニタイズして msi-logs へ保存する' -ForEach @(
        @{ Encoding = 'unicode' }
        @{ Encoding = 'utf8' }
    ) {
        $installLog = Join-Path $script:RunRoot 'msiexec-install.log'
        Set-Content -LiteralPath $installLog -Value 'ProductCode = {01234567-89AB-CDEF-0123-456789ABCDEF} インストール完了' -Encoding $Encoding

        $saved = Save-MsiLogArtifact -LogPath @($installLog) -ArtifactDirectory $script:ArtifactDirectory -Sanitize $script:Sanitize

        $saved | Should -Be @('msi-logs/msiexec-install.log')
        $content = Get-Content -LiteralPath (Join-Path $script:ArtifactDirectory 'msi-logs\msiexec-install.log') -Raw -Encoding utf8
        $content | Should -Match 'ProductCode = <redacted> インストール完了'
        $content | Should -Not -Match '01234567-89AB'
    }

    It '存在しないログは無視し、ディレクトリも作らない' {
        $saved = Save-MsiLogArtifact -LogPath @((Join-Path $script:RunRoot 'missing.log')) -ArtifactDirectory $script:ArtifactDirectory -Sanitize $script:Sanitize

        $saved.Count | Should -Be 0
        Join-Path $script:ArtifactDirectory 'msi-logs' | Should -Not -Exist
    }

    It 'install と uninstall のうち存在するログだけを保存する' {
        $installLog = Join-Path $script:RunRoot 'msiexec-install.log'
        Set-Content -LiteralPath $installLog -Value 'install' -Encoding unicode

        $saved = Save-MsiLogArtifact `
            -LogPath @($installLog, (Join-Path $script:RunRoot 'msiexec-uninstall.log')) `
            -ArtifactDirectory $script:ArtifactDirectory `
            -Sanitize $script:Sanitize

        $saved | Should -Be @('msi-logs/msiexec-install.log')
    }
}
