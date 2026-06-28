# Pester v5 tests for extract-release-notes.ps1
# 公開 release 本文の主要経路の回帰防止。版系列境界・前版選択・セクション名マップを固定する。

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'extract-release-notes.ps1'
    $script:WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) ("rn-tests-" + [guid]::NewGuid())
    New-Item -ItemType Directory -Path $script:WorkDir -Force | Out-Null

    # 合成 CHANGELOG: 版系列境界（0.1.0 の下に旧系列 3.1.0）を含む
    $changelog = @'
# Changelog

## [Unreleased]

## [0.1.3] - 2026-06-27

### Added
- 機能A を追加（#1）

### Fixed
- 不具合B を修正（#2）

## [0.1.2] - 2026-06-21

### Changed
- 変更C（#3）

## [0.1.0] - 2026-06-21

### Added
- 初回リリース（#4）

## [3.1.0] - 2025-12-15

### Added
- 旧系列のエントリ

[Unreleased]: https://example.com/compare/v0.1.3...HEAD
[0.1.3]: https://example.com/compare/v0.1.2...v0.1.3
[0.1.2]: https://example.com/compare/v0.1.1...v0.1.2
[0.1.0]: https://example.com/releases/tag/v0.1.0
[3.1.0]: https://example.com/compare/v3.0.0...v3.1.0
'@
    $script:ChangelogPath = Join-Path $script:WorkDir 'CHANGELOG.md'
    Set-Content -LiteralPath $script:ChangelogPath -Value $changelog -Encoding utf8

    $script:TemplatePath = Join-Path $script:WorkDir 'TEMPLATE.md'
    Set-Content -LiteralPath $script:TemplatePath -Value "## インストール`n{{VERSION}}" -Encoding utf8

    function Invoke-Gen([string]$Version) {
        $out = Join-Path $script:WorkDir ("out-$Version.md")
        & $script:ScriptPath -Version $Version `
            -ChangelogPath $script:ChangelogPath `
            -TemplatePath $script:TemplatePath `
            -OutPath $out | Out-Null
        return (Get-Content -LiteralPath $out -Raw)
    }
}

AfterAll {
    if ($script:WorkDir -and (Test-Path $script:WorkDir)) {
        Remove-Item -Recurse -Force $script:WorkDir
    }
}

Describe 'extract-release-notes' {
    It '前版が無い最古版 (0.1.0) は Full Changelog リンクを出さない' {
        $r = Invoke-Gen '0.1.0'
        $r | Should -Not -Match 'Full Changelog'
    }

    It '版系列境界を跨がず正しい直前版を選ぶ (0.1.3 -> 0.1.2)' {
        $r = Invoke-Gen '0.1.3'
        $r | Should -Match 'compare/v0\.1\.2\.\.\.v0\.1\.3'
        $r | Should -Not -Match 'v3\.1\.0'
    }

    It 'セクション名を日本語へマップする' {
        $r = Invoke-Gen '0.1.3'
        $r | Should -Match '### 追加'
        $r | Should -Match '### 修正'
        $r | Should -Not -Match '### Added'
    }

    It '最後のバージョン節でフッタのリンク定義を本文に取り込まない' {
        $r = Invoke-Gen '3.1.0'
        $r | Should -Not -Match '(?m)^\[3\.1\.0\]:'
        $r | Should -Not -Match '(?m)^\[Unreleased\]:'
    }
}
