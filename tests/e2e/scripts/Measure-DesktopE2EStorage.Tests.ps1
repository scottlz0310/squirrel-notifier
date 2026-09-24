# Pester v5 tests for Measure-DesktopE2EStorage.ps1
# 測定用の外部コマンドが失敗しても job を失敗させず、終了コードと stderr を storage.json に残すことを固定する。
# GitHub Actions の shell: pwsh はスクリプト末尾の $LASTEXITCODE を step の終了コードにする（run 35932153754）。
# docker / dotnet / wix は、引数ごとの終了コードを返す global 関数で置き換える。

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'Measure-DesktopE2EStorage.ps1'

    function global:Invoke-FakeTool
    {
        param([string]$Name, [object[]]$ToolArgs)

        $key = "$Name $($ToolArgs[0])"
        $exitCode = $global:FakeExitCodes[$key]
        $global:LASTEXITCODE = $exitCode
        if ($exitCode -eq 0)
        {
            return "$Name output"
        }
    }

    function global:docker { Invoke-FakeTool -Name 'docker' -ToolArgs $args }
    function global:dotnet { Invoke-FakeTool -Name 'dotnet' -ToolArgs $args }
    function global:wix { Invoke-FakeTool -Name 'wix' -ToolArgs $args }

    function Invoke-Measure
    {
        $workspace = Join-Path $TestDrive 'workspace'
        New-Item -ItemType Directory -Path $workspace -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $workspace 'file.txt') -Value 'x'
        $outputPath = Join-Path $TestDrive 'storage.json'

        & $script:ScriptPath -OutputPath $outputPath -WorkspacePath $workspace 3>$null 6>$null
        Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json
    }
}

AfterAll {
    Remove-Item -Path 'function:global:docker', 'function:global:dotnet', 'function:global:wix', 'function:global:Invoke-FakeTool' -ErrorAction SilentlyContinue
    Remove-Variable -Name 'FakeExitCodes' -Scope Global -ErrorAction SilentlyContinue
}

Describe 'Measure-DesktopE2EStorage.ps1' {
    BeforeEach {
        $global:FakeExitCodes = @{
            'docker --version' = 0
            'dotnet --version' = 0
            'wix --version'    = 0
            'docker system'    = 0
        }
    }

    It '<Command> が <ExitCode> で失敗しても $LASTEXITCODE を 0 で終え、終了コードを記録する' -ForEach @(
        @{ Command = 'docker system'; ExitCode = 1; Path = 'dockerDiskUsageCommand' }
        @{ Command = 'docker --version'; ExitCode = 1; Path = 'tools.docker' }
        @{ Command = 'dotnet --version'; ExitCode = 2; Path = 'tools.dotnet' }
        @{ Command = 'wix --version'; ExitCode = 3; Path = 'tools.wix' }
    ) {
        $global:FakeExitCodes[$Command] = $ExitCode

        $report = Invoke-Measure

        $global:LASTEXITCODE | Should -Be 0
        $entry = $report
        foreach ($name in $Path.Split('.')) { $entry = $entry.$name }
        $entry.exitCode | Should -Be $ExitCode
    }

    It 'すべて成功すれば出力と終了コード 0 を記録する' {
        $report = Invoke-Measure

        $global:LASTEXITCODE | Should -Be 0
        $report.tools.wix.version | Should -Be 'wix output'
        $report.tools.wix.exitCode | Should -Be 0
        @($report.dockerDiskUsage) | Should -Be @('docker output')
        $report.dockerDiskUsageCommand.exitCode | Should -Be 0
    }
}
