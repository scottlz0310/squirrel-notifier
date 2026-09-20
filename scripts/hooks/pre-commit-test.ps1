<#
.SYNOPSIS
    Pre-push hook to run tests with coverage.

.DESCRIPTION
    This script runs the same coverage selection as CI before push.
    Tests must pass and coverage must meet the 80% threshold.
#>

$ErrorActionPreference = "Stop"

$solutionPath = "winui3/SquirrelNotifier.WinUI3.sln"
$timeoutSeconds = 600
$timeoutOverride = [Environment]::GetEnvironmentVariable("SQUIRREL_NOTIFIER_PRE_PUSH_TIMEOUT_SECONDS")

if (-not [string]::IsNullOrWhiteSpace($timeoutOverride)) {
    $parsedTimeout = 0
    if (-not [int]::TryParse($timeoutOverride, [ref]$parsedTimeout) -or $parsedTimeout -le 0) {
        throw "SQUIRREL_NOTIFIER_PRE_PUSH_TIMEOUT_SECONDS は 1 以上の整数で指定してください。"
    }

    $timeoutSeconds = $parsedTimeout
}

function Get-TaskOutput {
    param(
        [Parameter(Mandatory = $true)]
        [System.Threading.Tasks.Task[string]]$Task,

        [Parameter(Mandatory = $true)]
        [string]$StreamName
    )

    try {
        if (-not $Task.Wait([TimeSpan]::FromSeconds(5))) {
            return "[$StreamName の読み取りが完了しませんでした。]"
        }

        return $Task.GetAwaiter().GetResult()
    } catch {
        return "[$StreamName の読み取りに失敗しました: $($_.Exception.Message)]"
    }
}

function Invoke-DotnetTest {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [int]$TimeoutSeconds
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = "dotnet"
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $timedOut = $false

    try {
        if (-not $process.Start()) {
            throw "dotnet test プロセスを起動できませんでした。"
        }

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $completed = $process.WaitForExit($TimeoutSeconds * 1000)

        if (-not $completed) {
            $timedOut = $true
            try {
                $process.Kill($true)
            } catch [InvalidOperationException] {
                # 終了競合時は既にプロセスが終了しているため、そのまま出力を回収する。
            }

            [void]$process.WaitForExit(5000)
        }

        $stdout = Get-TaskOutput -Task $stdoutTask -StreamName "標準出力"
        $stderr = Get-TaskOutput -Task $stderrTask -StreamName "標準エラー出力"
        $output = (($stdout, $stderr) -join [Environment]::NewLine).Trim()

        return [pscustomobject]@{
            ExitCode = if ($process.HasExited) { $process.ExitCode } else { -1 }
            TimedOut = $timedOut
            Output = $output
        }
    } finally {
        $process.Dispose()
    }
}

$testArguments = @(
    "test",
    $solutionPath,
    "--configuration", "Release",
    "--no-build",
    "--verbosity", "normal",
    "--collect:XPlat Code Coverage",
    "--results-directory", "./coverage",
    "/p:Platform=x64",
    "/p:CollectCoverage=true",
    "/p:CoverletOutputFormat=opencover",
    "/p:ThresholdType=line%2cbranch%2cmethod",
    "/p:Threshold=80"
)

Write-Host "Running tests with coverage (line / branch / method, timeout: $($timeoutSeconds)s)..." -ForegroundColor Cyan

try {
    $result = Invoke-DotnetTest -Arguments $testArguments -TimeoutSeconds $timeoutSeconds

    if ($result.TimedOut) {
        Write-Host ""
        Write-Host "ERROR: dotnet test が $timeoutSeconds 秒以内に終了しませんでした。プロセスツリーを終了しました。" -ForegroundColor Red
        Write-Host ""
        Write-Host "Output:" -ForegroundColor Yellow
        Write-Host $result.Output
        Write-Host ""
        Write-Host "subscriber / testhost の終了待ち、または fixture の環境差を確認し、次の実行条件で再試行してください。" -ForegroundColor Yellow
        Write-Host "  pwsh -File scripts/hooks/pre-commit-test.ps1" -ForegroundColor White
        Write-Host ""
        exit 1
    }

    if ($result.ExitCode -ne 0) {
        Write-Host ""
        Write-Host "ERROR: Tests failed or coverage is below 80%!" -ForegroundColor Red
        Write-Host ""
        Write-Host "Output:" -ForegroundColor Yellow
        Write-Host $result.Output
        Write-Host ""
        Write-Host "Please fix the failing tests or increase coverage before pushing." -ForegroundColor Yellow
        Write-Host ""
        Write-Host "再実行: pwsh -File scripts/hooks/pre-commit-test.ps1" -ForegroundColor Cyan
        Write-Host ""
        exit $result.ExitCode
    }

    Write-Host "✓ All tests passed with sufficient coverage" -ForegroundColor Green
    exit 0
} catch {
    Write-Host "ERROR: Failed to run tests" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
