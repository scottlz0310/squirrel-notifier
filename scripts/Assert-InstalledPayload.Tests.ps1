BeforeAll {
    . (Join-Path $PSScriptRoot 'Assert-InstalledPayload.ps1')
}

Describe 'インストール先の全 payload 照合' {
    BeforeEach {
        $source = New-Item -ItemType Directory -Path (Join-Path $TestDrive ([guid]::NewGuid().ToString('N')))
        $target = New-Item -ItemType Directory -Path (Join-Path $TestDrive ([guid]::NewGuid().ToString('N')))
        New-Item -ItemType Directory -Path (Join-Path $source 'sub') | Out-Null
        Set-Content (Join-Path $source 'app.exe') 'application'
        Set-Content (Join-Path $source 'sub/runtime.dll') 'runtime'
        Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse
    }

    It 'サブディレクトリを含む全ファイルを照合する' {
        Assert-InstalledPayload -PayloadDirectory $source -InstallDirectory $target | Should -Be 2
    }

    It 'ファイルの欠落と同じ長さの改変を拒否する: <Case>' -ForEach @(
        @{ Case = 'missing'; Message = '*欠落*sub*runtime.dll*' }
        @{ Case = 'changed'; Message = '*SHA256*sub*runtime.dll*' }
    ) {
        $path = Join-Path $target 'sub/runtime.dll'
        if ($Case -eq 'missing') { Remove-Item -LiteralPath $path }
        else { Set-Content $path 'changed' }
        { Assert-InstalledPayload -PayloadDirectory $source -InstallDirectory $target } | Should -Throw $Message
    }

    It '比較元が空なら成功扱いにしない' {
        $empty = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'empty')
        { Assert-InstalledPayload -PayloadDirectory $empty -InstallDirectory $target } | Should -Throw '*payload が空*'
    }
}
