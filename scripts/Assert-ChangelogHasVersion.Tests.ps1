# Pester v5 tests for Assert-ChangelogHasVersion.ps1
# changelog-guard.yml の照合。<Version> の欠落・節の欠落・前方一致の誤判定（[1.2.3] と [1.2.30]）を固定する。

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'Assert-ChangelogHasVersion.ps1'

    function script:Invoke-Guard([string]$CsprojContent, [string[]]$ChangelogLines) {
        $csproj = Join-Path $TestDrive 'app.csproj'
        $changelog = Join-Path $TestDrive 'CHANGELOG.md'
        Set-Content -LiteralPath $csproj -Value $CsprojContent
        Set-Content -LiteralPath $changelog -Value $ChangelogLines
        & $script:ScriptPath -CsprojPath $csproj -ChangelogPath $changelog 6> $null
    }

    function script:New-Csproj([string]$Version) {
        "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><Version>$Version</Version></PropertyGroup></Project>"
    }
}

Describe 'Assert-ChangelogHasVersion' {
    It '<Name> なら通す' -ForEach @(
        @{ Name = '節がある'; Heading = '## [1.2.3] - 2026-09-27' }
        @{ Name = '見出しの空白が無い'; Heading = '##[1.2.3]' }
        @{ Name = 'Version 要素の前後に空白がある'; Heading = '## [1.2.3]'; Version = ' 1.2.3 ' }
    ) {
        $v = if ($Version) { $Version } else { '1.2.3' }

        { Invoke-Guard (New-Csproj $v) @('# Changelog', '', '## [Unreleased]', '', $Heading) } | Should -Not -Throw
    }

    It '<Name> なら失敗する' -ForEach @(
        @{ Name = '節が無い'; Lines = @('## [Unreleased]', '## [1.2.2] - 2026-09-01') }
        @{ Name = '[1.2.30] だけがある'; Lines = @('## [1.2.30] - 2026-09-01') }
        @{ Name = '比較リンクだけがある'; Lines = @('[1.2.3]: https://example.com/compare/v1.2.2...v1.2.3') }
    ) {
        { Invoke-Guard (New-Csproj '1.2.3') $Lines } |
            Should -Throw ('*' + [WildcardPattern]::Escape('CHANGELOG.md に [1.2.3] の節がありません') + '*')
    }

    It 'csproj に Version 要素が無ければ失敗する' {
        { Invoke-Guard '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup /></Project>' @('## [1.2.3]') } |
            Should -Throw '*csproj から <Version> を取得できません*'
    }
}
