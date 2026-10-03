BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'Assert-ReleaseCommit.ps1'
}

Describe 'リリースコミットの受付' {
    BeforeEach {
        Push-Location $TestDrive
        Set-Content project.csproj '<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>'
        Set-Content CHANGELOG.md '## [1.2.3] - 2026-10-03'
    }
    AfterEach { Pop-Location }

    It '安定版のタイトルと version と原稿が一致すれば通す' {
        { & $script:ScriptPath -Message "chore(release): v1.2.3`n`n準備 PR" -CsprojPath project.csproj } | Should -Not -Throw
    }

    It '不正なタイトルをタグ作成前に拒否する: <Message>' -ForEach @(
        @{ Message = 'chore(release): v1.2.3-rc.1' }
        @{ Message = 'chore(release): v1.2.3+build' }
        @{ Message = 'chore(release): v1.2.3 extra' }
        @{ Message = 'chore(release): v1.2' }
        @{ Message = 'feat: v1.2.3' }
    ) {
        { & $script:ScriptPath -Message $Message -CsprojPath project.csproj } | Should -Throw '*安定版形式*'
    }

    It 'version の不一致と欠落と重複を拒否する' -ForEach @(
        @{ Xml = '<Project><Version>1.2.4</Version></Project>' }
        @{ Xml = '<Project />' }
        @{ Xml = '<Project><Version>1.2.3</Version><Version>1.2.3</Version></Project>' }
    ) {
        Set-Content project.csproj $Xml
        { & $script:ScriptPath -Message 'chore(release): v1.2.3' -CsprojPath project.csproj } | Should -Throw '*一致しません*'
    }

    It '原稿が無ければ拒否する' {
        Set-Content CHANGELOG.md '## [1.2.30]'
        { & $script:ScriptPath -Message 'chore(release): v1.2.3' -CsprojPath project.csproj } | Should -Throw '*節がありません*'
    }
}
