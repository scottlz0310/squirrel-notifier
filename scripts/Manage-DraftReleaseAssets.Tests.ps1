BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'Manage-DraftReleaseAssets.ps1'
    function gh { param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments) }
    function Invoke-Assets([string]$Action = 'Attach') {
        & $script:ScriptPath -Action $Action -Repository 'owner/repo' -Version '1.2.3' -TargetSha ('a' * 40) -Directory $script:Directory -NotesPath $script:NotesPath
    }
}
AfterAll {
    Remove-Variable -Scope Global -Name DraftAssetsTestRelease, DraftAssetsTestFailure
}
Describe 'draft の添付と再取得' {
    BeforeEach {
        $script:Directory = Join-Path $TestDrive ('assets ' + [guid]::NewGuid())
        $script:NotesPath = Join-Path $TestDrive 'notes.md'
        Set-Content $script:NotesPath '日本語ノート'
        $global:DraftAssetsTestRelease = @{ isDraft = $true; tagName = 'v1.2.3'; targetCommitish = 'a' * 40; body = '日本語ノート' }
        $global:DraftAssetsTestFailure = ''
        Mock gh {
            $global:LASTEXITCODE = if ($Arguments[1] -eq $global:DraftAssetsTestFailure) { 1 } else { 0 }
            if ($Arguments[1] -eq 'view') { $global:DraftAssetsTestRelease | ConvertTo-Json }
        }
        New-Item -ItemType Directory -Path $script:Directory | Out-Null
        $zip = Join-Path $script:Directory 'SquirrelNotifier-Setup-1.2.3-x64.zip'
        $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($name in @('SquirrelNotifier.WinUI3.exe', 'install.ps1', 'install.cmd', 'uninstall.ps1', 'uninstall.cmd', 'create-shortcuts.ps1', 'create-shortcuts.cmd')) {
                $archive.CreateEntry($name) | Out-Null
            }
        }
        finally { $archive.Dispose() }
        Set-Content (Join-Path $script:Directory 'SquirrelNotifier-Setup-1.2.3-x64.msi') 'MSI fixture'
        @('SquirrelNotifier-Setup-1.2.3-x64.zip', 'SquirrelNotifier-Setup-1.2.3-x64.msi') | ForEach-Object {
            "$((Get-FileHash -LiteralPath (Join-Path $script:Directory $_)).Hash)  $_"
        } | Set-Content (Join-Path $script:Directory 'checksums-x64.txt')
    }
    It '公開済みと異なるタグと異なる SHA は更新しない: <Field>' -ForEach @(
        @{ Field = 'isDraft'; Value = $false }
        @{ Field = 'tagName'; Value = 'v1.2.4' }
        @{ Field = 'targetCommitish'; Value = 'b' * 40 }
    ) {
        $global:DraftAssetsTestRelease[$Field] = $Value
        { Invoke-Assets } | Should -Throw '*固定 SHA の draft*'
        Should -Invoke gh -Times 1 -Exactly
    }
    It 'draft 取得の失敗を伝える' {
        $global:DraftAssetsTestFailure = 'view'
        { Invoke-Assets } | Should -Throw '*取得できません*'
    }
    It 'ノートが合成原稿と異なればダウンロードしない' {
        $global:DraftAssetsTestRelease.body = '異なるノート'
        { Invoke-Assets Verify } | Should -Throw '*合成結果と一致しません*'
        Should -Invoke gh -Times 1 -Exactly
    }
    It 'ダウンロードの失敗を伝える' {
        $global:DraftAssetsTestFailure = 'download'
        { Invoke-Assets Verify } | Should -Throw '*成果物を取得できません*'
        Should -Invoke gh -Times 2 -Exactly
    }
    It '成果物が無ければアップロードしない' {
        $script:Directory = Join-Path $TestDrive 'missing-assets'
        { Invoke-Assets } | Should -Throw
        Should -Invoke gh -Times 1 -Exactly
    }
    It '固定 SHA の draft に成果物と日本語ノートを添付する' {
        { Invoke-Assets } | Should -Not -Throw
        Should -Invoke gh -Times 1 -Exactly -ParameterFilter { $Arguments[1] -eq 'upload' -and $Arguments -contains '--clobber' }
        Should -Invoke gh -Times 1 -Exactly -ParameterFilter { $Arguments[1] -eq 'edit' -and $Arguments -contains '--notes-file' }
    }
    It '添付とノート更新の CLI 失敗を伝える: <Operation>' -ForEach @(
        @{ Operation = 'upload'; Error = '*成果物添付に失敗*'; Calls = 2 }
        @{ Operation = 'edit'; Error = '*日本語ノート更新に失敗*'; Calls = 3 }
    ) {
        $global:DraftAssetsTestFailure = $Operation
        { Invoke-Assets } | Should -Throw $Error
        Should -Invoke gh -Times $Calls -Exactly
    }
}
