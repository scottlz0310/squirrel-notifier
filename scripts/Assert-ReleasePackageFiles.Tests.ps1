BeforeAll {
    . (Join-Path $PSScriptRoot 'Assert-ReleasePackageFiles.ps1')
    function New-TestPackage([string]$Directory, [string]$Missing = '', [bool]$Duplicate = $false) {
        New-Item -ItemType Directory -Path $Directory -Force | Out-Null
        $zip = Join-Path $Directory 'SquirrelNotifier-Setup-1.2.3-x64.zip'
        $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($name in @('SquirrelNotifier.WinUI3.exe', 'install.ps1', 'install.cmd', 'uninstall.ps1', 'uninstall.cmd', 'create-shortcuts.ps1', 'create-shortcuts.cmd')) {
                if ($name -ne $Missing) { $archive.CreateEntry($name) | Out-Null }
            }
            if ($Duplicate) { $archive.CreateEntry('install.ps1') | Out-Null }
        }
        finally { $archive.Dispose() }
        Set-Content (Join-Path $Directory 'SquirrelNotifier-Setup-1.2.3-x64.msi') 'MSI fixture'
        Set-TestChecksums $Directory
    }
    function Set-TestChecksums([string]$Directory) {
        @('SquirrelNotifier-Setup-1.2.3-x64.zip', 'SquirrelNotifier-Setup-1.2.3-x64.msi') | ForEach-Object {
            "$((Get-FileHash -LiteralPath (Join-Path $Directory $_)).Hash)  $_"
        } | Set-Content (Join-Path $Directory 'checksums-x64.txt')
    }
}

Describe '添付する配布物の整合性' {
    BeforeEach {
        $script:Directory = Join-Path $TestDrive ('package with spaces ' + [guid]::NewGuid())
        New-TestPackage $script:Directory
    }
    It '必須 ZIP 内容と ZIP/MSI の checksum を確認する' {
        { Assert-ReleasePackageFiles -Directory $script:Directory -Version '1.2.3' } | Should -Not -Throw
    }
    It '配布物の改ざんを拒否する: <Name>' -ForEach @(
        @{ Name = 'SquirrelNotifier-Setup-1.2.3-x64.zip' }
        @{ Name = 'SquirrelNotifier-Setup-1.2.3-x64.msi' }
    ) {
        Add-Content (Join-Path $script:Directory $Name) 'changed'
        { Assert-ReleasePackageFiles -Directory $script:Directory -Version '1.2.3' } | Should -Throw '*checksum が一致しません*'
    }
    It 'checksum の形式と対象と重複と欠落を拒否する: <Case>' -ForEach @(
        @{ Case = 'format' }
        @{ Case = 'unknown' }
        @{ Case = 'duplicate' }
        @{ Case = 'missing' }
    ) {
        $path = Join-Path $script:Directory 'checksums-x64.txt'
        $lines = @(Get-Content $path)
        switch ($Case) {
            format { Set-Content $path 'invalid checksum' }
            unknown { Add-Content $path ((('a' * 64) + '  ../other.msi')) }
            duplicate { Add-Content $path $lines[0] }
            missing { Set-Content $path $lines[0] }
        }
        { Assert-ReleasePackageFiles -Directory $script:Directory -Version '1.2.3' } | Should -Throw '*checksum*'
    }
    It '必要な内容の欠落と重複を拒否する: <Missing>' -ForEach @(
        @{ Missing = 'SquirrelNotifier.WinUI3.exe'; Duplicate = $false }
        @{ Missing = 'install.cmd'; Duplicate = $false }
        @{ Missing = ''; Duplicate = $true }
    ) {
        $path = Join-Path $TestDrive ([guid]::NewGuid().ToString())
        New-TestPackage $path -Missing $Missing -Duplicate $Duplicate
        { Assert-ReleasePackageFiles -Directory $path -Version '1.2.3' } | Should -Throw '*一意に存在しません*'
    }
}
