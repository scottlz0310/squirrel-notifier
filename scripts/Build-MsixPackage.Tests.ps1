BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'Build-MsixPackage.ps1'
    $script:ProjectPath = Join-Path $PSScriptRoot '../winui3/SquirrelNotifier.WinUI3/SquirrelNotifier.WinUI3.csproj'

    # PE ヘッダーの machine だけを持つ最小の実行ファイルを作る。
    function New-FakeExecutable([string]$Path, [int]$Machine) {
        $bytes = [byte[]]::new(0x46)
        [System.Text.Encoding]::ASCII.GetBytes('MZ').CopyTo($bytes, 0)
        [System.BitConverter]::GetBytes([int]0x40).CopyTo($bytes, 0x3C)
        [System.Text.Encoding]::ASCII.GetBytes("PE`0`0").CopyTo($bytes, 0x40)
        [System.BitConverter]::GetBytes([uint16]$Machine).CopyTo($bytes, 0x44)
        [System.IO.File]::WriteAllBytes($Path, $bytes)
    }
}

AfterAll {
    Remove-Item Env:MSIX_TEST_LOG, Env:MSIX_TEST_PACK_EXIT, Env:MSIX_TEST_SIGN_EXIT -ErrorAction SilentlyContinue
}

Describe 'MSIX パッケージの生成' {
    BeforeEach {
        $script:Root = Join-Path $TestDrive ('msix with spaces ' + [guid]::NewGuid())
        $script:Payload = Join-Path $script:Root 'payload'
        $script:Output = Join-Path $script:Root 'output'
        $script:Log = Join-Path $script:Root 'log'
        New-Item -ItemType Directory -Path $script:Payload, $script:Log | Out-Null
        $script:Executable = Join-Path $script:Payload 'SquirrelNotifier.WinUI3.exe'
        New-FakeExecutable $script:Executable 0x8664
        Set-Content -LiteralPath (Join-Path $script:Payload 'dependency.dll') -Value 'dll'

        # 実ツールの代わりに、引数と staging の内容を記録する。
        $script:MakeAppx = Join-Path $script:Root 'makeappx.ps1'
        Set-Content -LiteralPath $script:MakeAppx -Value @'
$args | Set-Content -LiteralPath (Join-Path $env:MSIX_TEST_LOG 'makeappx.args')
$staging = $args[[Array]::IndexOf($args, '/d') + 1]
Copy-Item -LiteralPath (Join-Path $staging 'AppxManifest.xml') -Destination $env:MSIX_TEST_LOG
Get-ChildItem -LiteralPath $staging -Recurse -File | ForEach-Object { $_.FullName.Substring($staging.Length + 1).Replace('\', '/') } |
    Set-Content -LiteralPath (Join-Path $env:MSIX_TEST_LOG 'staging.files')
Set-Content -LiteralPath $args[[Array]::IndexOf($args, '/p') + 1] -Value 'package'
exit [int]$env:MSIX_TEST_PACK_EXIT
'@
        $script:SignTool = Join-Path $script:Root 'signtool.ps1'
        Set-Content -LiteralPath $script:SignTool -Value @'
$args | Set-Content -LiteralPath (Join-Path $env:MSIX_TEST_LOG 'signtool.args')
Add-Content -LiteralPath $args[-1] -Value 'signed'
exit [int]$env:MSIX_TEST_SIGN_EXIT
'@
        $env:MSIX_TEST_LOG = $script:Log
        $env:MSIX_TEST_PACK_EXIT = '0'
        $env:MSIX_TEST_SIGN_EXIT = '0'
        $script:Common = @{
            PayloadDirectory = $script:Payload
            OutputDirectory  = $script:Output
            MakeAppxPath     = $script:MakeAppx
            SignToolPath     = $script:SignTool
        }
    }

    It '版を 4 要素にしたマニフェストと、発行物・ロゴをパッケージへ入れる' {
        $package = & $script:ScriptPath @script:Common -Version '1.2.3' 6> $null
        $package | Should -Be (Join-Path $script:Output 'SquirrelNotifier-1.2.3-x64.msix')
        Test-Path -LiteralPath $package | Should -BeTrue
        [xml]$manifest = Get-Content -LiteralPath (Join-Path $script:Log 'AppxManifest.xml') -Raw
        $manifest.Package.Identity.Version | Should -Be '1.2.3.0'
        $files = Get-Content -LiteralPath (Join-Path $script:Log 'staging.files')
        foreach ($name in @('SquirrelNotifier.WinUI3.exe', 'dependency.dll', 'AppxManifest.xml', 'Assets/StoreLogo.png', 'Assets/Square44x44Logo.png', 'Assets/Square150x150Logo.png')) {
            $files | Should -Contain $name
        }
        Test-Path -LiteralPath (Join-Path $script:Log 'signtool.args') | Should -BeFalse
        @(Get-ChildItem -LiteralPath $script:Output -Directory).Count | Should -Be 0
    }

    It 'マニフェストが参照するロゴは、すべてパッケージに含まれる' {
        & $script:ScriptPath @script:Common -Version '1.2.3' 6> $null | Out-Null
        [xml]$manifest = Get-Content -LiteralPath (Join-Path $script:Log 'AppxManifest.xml') -Raw
        $files = Get-Content -LiteralPath (Join-Path $script:Log 'staging.files')
        $visual = $manifest.Package.Applications.Application.VisualElements
        foreach ($logo in @($manifest.Package.Properties.Logo, $visual.Square150x150Logo, $visual.Square44x44Logo)) {
            $files | Should -Contain $logo.Replace('\', '/')
        }
    }

    It 'version 未指定では csproj の version を使う' {
        [xml]$project = Get-Content -LiteralPath $script:ProjectPath -Raw
        $expected = @($project.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
        $package = & $script:ScriptPath @script:Common 6> $null
        Split-Path -Leaf $package | Should -Be "SquirrelNotifier-$expected-x64.msix"
    }

    It '署名の引数を組み立てる: <Name>' -ForEach @(
        @{ Name = 'タイムスタンプなし'; Url = ''; Expected = @('sign', '/fd', 'SHA256', '/sha1', 'ABC123') }
        @{ Name = 'タイムスタンプあり'; Url = 'http://timestamp.example'; Expected = @('sign', '/fd', 'SHA256', '/sha1', 'ABC123', '/tr', 'http://timestamp.example', '/td', 'SHA256') }
    ) {
        $package = & $script:ScriptPath @script:Common -Version '1.2.3' -CertificateThumbprint 'ABC123' -TimestampUrl $Url 6> $null
        $arguments = @(Get-Content -LiteralPath (Join-Path $script:Log 'signtool.args'))
        $arguments[0..($arguments.Count - 2)] | Should -Be $Expected
        # 署名は一時パスに行い、署名済みのものを最終パスへ置く。
        $arguments[-1] | Should -Not -Be $package
        Split-Path -Leaf $arguments[-1] | Should -Be (Split-Path -Leaf $package)
        Get-Content -LiteralPath $package | Should -Contain 'signed'
        @(Get-ChildItem -LiteralPath $script:Output).Count | Should -Be 1
    }

    It '成功したら、同じ版の既存パッケージを置き換える' {
        New-Item -ItemType Directory -Path $script:Output | Out-Null
        $existing = Join-Path $script:Output 'SquirrelNotifier-1.2.3-x64.msix'
        Set-Content -LiteralPath $existing -Value 'previous'
        & $script:ScriptPath @script:Common -Version '1.2.3' 6> $null | Out-Null
        Get-Content -LiteralPath $existing | Should -Be 'package'
    }

    It '<Stage>に失敗したら止まり、新しい成果物を残さない' -ForEach @(
        @{ Stage = '生成'; PackExit = '1'; SignExit = '0'; Message = '*MSIX の生成に失敗しました*' }
        @{ Stage = '署名'; PackExit = '0'; SignExit = '1'; Message = '*MSIX の署名に失敗しました*' }
    ) {
        $env:MSIX_TEST_PACK_EXIT = $PackExit
        $env:MSIX_TEST_SIGN_EXIT = $SignExit
        { & $script:ScriptPath @script:Common -Version '1.2.3' -CertificateThumbprint 'ABC123' } | Should -Throw $Message
        @(Get-ChildItem -LiteralPath $script:Output).Count | Should -Be 0
    }

    It '<Stage>に失敗しても、同じ版の既存パッケージを変えない' -ForEach @(
        @{ Stage = '生成'; PackExit = '1'; SignExit = '0' }
        @{ Stage = '署名'; PackExit = '0'; SignExit = '1' }
    ) {
        New-Item -ItemType Directory -Path $script:Output | Out-Null
        $existing = Join-Path $script:Output 'SquirrelNotifier-1.2.3-x64.msix'
        Set-Content -LiteralPath $existing -Value 'previous'
        $hash = (Get-FileHash -LiteralPath $existing).Hash
        $env:MSIX_TEST_PACK_EXIT = $PackExit
        $env:MSIX_TEST_SIGN_EXIT = $SignExit
        { & $script:ScriptPath @script:Common -Version '1.2.3' -CertificateThumbprint 'ABC123' } | Should -Throw '*に失敗しました*'
        (Get-FileHash -LiteralPath $existing).Hash | Should -Be $hash
        @(Get-ChildItem -LiteralPath $script:Output).Count | Should -Be 1
    }

    It 'マニフェストと異なるアーキテクチャの実行ファイルは拒否する: <Name>' -ForEach @(
        @{ Name = 'x86'; Machine = 0x014C }
        @{ Name = 'Arm64'; Machine = 0xAA64 }
    ) {
        New-FakeExecutable $script:Executable $Machine
        { & $script:ScriptPath @script:Common -Version '1.2.3' } | Should -Throw '*x64 の実行ファイルではありません*'
        Test-Path -LiteralPath (Join-Path $script:Log 'makeappx.args') | Should -BeFalse
    }

    It 'PE 形式でない実行ファイルは拒否する: <Name>' -ForEach @(
        @{ Name = 'テキスト'; Content = 'not an executable' }
        @{ Name = '空'; Content = '' }
    ) {
        Set-Content -LiteralPath $script:Executable -Value $Content -NoNewline
        { & $script:ScriptPath @script:Common -Version '1.2.3' } | Should -Throw '*PE 形式の実行ファイルではありません*'
        Test-Path -LiteralPath (Join-Path $script:Log 'makeappx.args') | Should -BeFalse
    }

    It '不正な入力はパッケージの生成前に拒否する: <Name>' -ForEach @(
        @{ Name = '版の形式'; Extra = @{ Version = '1.2' }; Message = '*x.y.z 形式*' }
        @{ Name = '署名なしのタイムスタンプ'; Extra = @{ Version = '1.2.3'; TimestampUrl = 'http://timestamp.example' }; Message = '*CertificateThumbprint と併せて*' }
    ) {
        { & $script:ScriptPath @script:Common @Extra } | Should -Throw $Message
        Test-Path -LiteralPath (Join-Path $script:Log 'makeappx.args') | Should -BeFalse
    }

    It '発行物に実行ファイルが無ければ生成を始めない' {
        Remove-Item -LiteralPath (Join-Path $script:Payload 'SquirrelNotifier.WinUI3.exe')
        { & $script:ScriptPath @script:Common -Version '1.2.3' } | Should -Throw '*SquirrelNotifier.WinUI3.exe がありません*'
        Test-Path -LiteralPath (Join-Path $script:Log 'makeappx.args') | Should -BeFalse
    }

    It 'SDK ツールが復元されていなければ、探した場所を示して止まる' {
        $original = $env:NUGET_PACKAGES
        $env:NUGET_PACKAGES = Join-Path $script:Root 'empty-nuget'
        try {
            { & $script:ScriptPath -PayloadDirectory $script:Payload -OutputDirectory $script:Output -Version '1.2.3' } |
                Should -Throw '*makeappx.exe が見つかりません*dotnet restore*'
        }
        finally {
            $env:NUGET_PACKAGES = $original
        }
    }
}
