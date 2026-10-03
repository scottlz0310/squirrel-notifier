BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'Build-ReleasePackage.ps1'
    $script:ProjectPath = Join-Path $PSScriptRoot '../winui3/SquirrelNotifier.WinUI3/SquirrelNotifier.WinUI3.csproj'
    function wix { param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments) }
    function dotnet { param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments) }
    function pwsh { param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments) }
}

AfterAll {
    Remove-Variable -Name ReleasePackageTestPublishExit, ReleasePackageTestBuildExit, ReleasePackageTestMajorUpgradeExit, ReleasePackageTestCreateMsi, ReleasePackageTestCalls -Scope Global
}

Describe 'release package の生成' {
    BeforeEach {
        $script:Output = Join-Path $TestDrive ('package with spaces ' + [guid]::NewGuid())
        $global:ReleasePackageTestPublishExit = 0
        $global:ReleasePackageTestBuildExit = 0
        $global:ReleasePackageTestMajorUpgradeExit = 0
        $global:ReleasePackageTestCreateMsi = $true
        $global:ReleasePackageTestCalls = [System.Collections.Generic.List[object]]::new()
        Mock Get-Command { [pscustomobject]@{ Name = 'wix' } } -ParameterFilter { $Name -eq 'wix' }
        Mock wix { '7.0.0'; $global:LASTEXITCODE = 0 }
        Mock dotnet {
            $global:ReleasePackageTestCalls.Add(@($Arguments))
            if ($Arguments[0] -eq 'publish') {
                $path = $Arguments[[Array]::IndexOf($Arguments, '--output') + 1]
                Set-Content -LiteralPath (Join-Path $path 'app.exe') -Value 'publish payload'
                $global:LASTEXITCODE = $global:ReleasePackageTestPublishExit
            }
            else {
                if ($global:ReleasePackageTestCreateMsi) {
                    $path = ($Arguments | Where-Object { $_ -like '/p:OutputPath=*' }) -replace '^/p:OutputPath=', ''
                    Set-Content -LiteralPath (Join-Path $path 'app.msi') -Value 'msi payload'
                }
                $global:LASTEXITCODE = $global:ReleasePackageTestBuildExit
            }
        }
        Mock pwsh {
            $global:ReleasePackageTestCalls.Add(@($Arguments))
            $global:LASTEXITCODE = $global:ReleasePackageTestMajorUpgradeExit
        }
    }

    It 'ZIP と MSI と checksum と manifest を整合する名前で生成する' {
        & $script:ScriptPath -Version '1.2.3' -OutputDirectory $script:Output 6> $null
        $manifest = Get-Content -LiteralPath (Join-Path $script:Output 'release-package.json') -Raw | ConvertFrom-Json
        $manifest.version | Should -Be '1.2.3'
        $manifest.platform | Should -Be 'x64'
        $manifest.publishDirectory.Replace('\', '/') | Should -Be 'publish/x64'
        $manifest.installerArchive | Should -Be 'SquirrelNotifier-Setup-1.2.3-x64.zip'
        $manifest.msi | Should -Be 'SquirrelNotifier-Setup-1.2.3-x64.msi'
        $checksums = @(Get-Content -LiteralPath (Join-Path $script:Output $manifest.checksums))
        $checksums.Count | Should -Be 2
        foreach ($name in @($manifest.installerArchive, $manifest.msi)) {
            $hash = (Get-FileHash -LiteralPath (Join-Path $script:Output $name) -Algorithm SHA256).Hash
            $checksums | Should -Contain "$hash  $name"
        }
        $expanded = Join-Path $TestDrive 'expanded'
        Expand-Archive -LiteralPath (Join-Path $script:Output $manifest.installerArchive) -DestinationPath $expanded -Force
        Get-Content -LiteralPath (Join-Path $expanded 'app.exe') | Should -Be 'publish payload'
        foreach ($name in @('install.ps1', 'install.cmd', 'uninstall.ps1', 'uninstall.cmd', 'create-shortcuts.ps1', 'create-shortcuts.cmd')) {
            Test-Path -LiteralPath (Join-Path $expanded $name) | Should -BeTrue
        }
        $global:ReleasePackageTestCalls.Count | Should -Be 3
        $global:ReleasePackageTestCalls[0] | Should -Contain '--self-contained'
        $global:ReleasePackageTestCalls[0] | Should -Contain '/p:Version=1.2.3'
        $global:ReleasePackageTestCalls[1] | Should -Contain '/p:PackageVersion=1.2.3'
        $global:ReleasePackageTestCalls[2] | Should -Contain '-MsiPath'
        $global:ReleasePackageTestCalls[2] | Should -Contain (Join-Path $script:Output 'msi/x64/app.msi')
    }

    It 'version 未指定では csproj の version を使う' {
        & $script:ScriptPath -OutputDirectory $script:Output 6> $null
        [xml]$project = Get-Content -LiteralPath $script:ProjectPath -Raw
        $expected = @($project.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
        $manifest = Get-Content -LiteralPath (Join-Path $script:Output 'release-package.json') -Raw | ConvertFrom-Json
        $manifest.version | Should -Be $expected
    }

    It '不正な version は外部 CLI の実行前に拒否する' -ForEach @(
        @{ Version = 'v1.2.3' }
        @{ Version = '1.2' }
        @{ Version = '1.2.3-rc.1' }
        @{ Version = '1.2.3; echo unexpected' }
    ) {
        { & $script:ScriptPath -Version $Version -OutputDirectory $script:Output } | Should -Throw '*SemVer の 3 要素形式ではありません*'
        Should -Invoke dotnet -Times 0 -Exactly
    }

    It 'csproj の version が一意でなければ生成を始めない' -ForEach @(
        @{ Xml = '<Project><PropertyGroup><Version /></PropertyGroup></Project>' }
        @{ Xml = '<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup><PropertyGroup><Version>1.2.4</Version></PropertyGroup></Project>' }
    ) {
        Mock Get-Content { $Xml } -ParameterFilter { $LiteralPath -eq $script:ProjectPath }
        { & $script:ScriptPath -OutputDirectory $script:Output } | Should -Throw '*release version を一意に取得できません*'
        Should -Invoke dotnet -Times 0 -Exactly
    }

    It 'WiX が無ければ生成を始めない' {
        Mock Get-Command { $null } -ParameterFilter { $Name -eq 'wix' }
        { & $script:ScriptPath -Version '1.2.3' -OutputDirectory $script:Output } | Should -Throw '*WiX CLI が見つかりません*'
        Should -Invoke dotnet -Times 0 -Exactly
    }

    It 'WiX の非対応バージョンでは生成を始めない' -ForEach @(
        @{ WixVersion = '6.0.0' }
        @{ WixVersion = '8.0.0' }
    ) {
        Mock wix { $WixVersion }
        { & $script:ScriptPath -Version '1.2.3' -OutputDirectory $script:Output } | Should -Throw '*WiX CLI 7.x が必要です*'
        Should -Invoke dotnet -Times 0 -Exactly
    }

    It '外部処理の失敗で止まり manifest を作らない' -ForEach @(
        @{ Stage = 'publish'; Message = '*release publish に失敗*'; DotnetCalls = 1; PwshCalls = 0 }
        @{ Stage = 'build'; Message = '*MSI build に失敗*'; DotnetCalls = 2; PwshCalls = 0 }
        @{ Stage = 'upgrade'; Message = '*MSI MajorUpgrade 検証に失敗*'; DotnetCalls = 2; PwshCalls = 1 }
        @{ Stage = 'missing'; Message = '*MSI が見つかりません*'; DotnetCalls = 2; PwshCalls = 0 }
    ) {
        switch ($Stage) {
            'publish' { $global:ReleasePackageTestPublishExit = 1 }
            'build' { $global:ReleasePackageTestBuildExit = 1 }
            'upgrade' { $global:ReleasePackageTestMajorUpgradeExit = 1 }
            'missing' { $global:ReleasePackageTestCreateMsi = $false }
        }
        { & $script:ScriptPath -Version '1.2.3' -OutputDirectory $script:Output } | Should -Throw $Message
        Should -Invoke dotnet -Times $DotnetCalls -Exactly
        Should -Invoke pwsh -Times $PwshCalls -Exactly
        Test-Path -LiteralPath (Join-Path $script:Output 'release-package.json') | Should -BeFalse
    }
}
