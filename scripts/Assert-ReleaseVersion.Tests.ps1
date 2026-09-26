# Pester v5 tests for Assert-ReleaseVersion.ps1
# reapply-release-notes.yml の入力検証。x.y.z 以外（先頭の v・prerelease・要素数違い・空文字）を拒否することを固定する。

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'Assert-ReleaseVersion.ps1'
}

Describe 'Assert-ReleaseVersion' {
    It '<Version> を受け付ける' -ForEach @(
        @{ Version = '1.2.3' }
        @{ Version = '0.14.0' }
        @{ Version = '10.20.30' }
    ) {
        { & $script:ScriptPath -Version $Version } | Should -Not -Throw
    }

    It "'<Version>' を拒否する" -ForEach @(
        @{ Version = '' }
        @{ Version = 'v1.2.3' }
        @{ Version = 'v1.2' }
        @{ Version = '1.2' }
        @{ Version = '1.2.3-rc1' }
        @{ Version = '1.2.3.4' }
        @{ Version = '1.2.3; rm -rf /' }
    ) {
        { & $script:ScriptPath -Version $Version } | Should -Throw "*version は x.y.z 形式で指定してください: '$Version'*"
    }
}
