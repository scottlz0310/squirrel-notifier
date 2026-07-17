// <copyright file="AgentProcessLaunchRegressionTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Text;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

// Environment.CurrentDirectory を変更するテストを含むため、他の collection と並列実行しない
[CollectionDefinition("agent-process-launch", DisableParallelization = true)]
public sealed class AgentProcessLaunchCollection
{
}

/// <summary>
/// 実プロセスを起動する回帰テスト（#186）。共通 factory 経由の <c>.exe</c> / <c>.cmd</c> / <c>.bat</c>
/// 起動と、タスクスケジューラ起動相当の初期 cwd（System32）から dummy agent を起動しても
/// 明示的な WorkingDirectory で実行されることを検証する.
/// </summary>
[Collection("agent-process-launch")]
public class AgentProcessLaunchRegressionTests : IDisposable
{
    private readonly string _tempDir;

    public AgentProcessLaunchRegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"AgentProcessLaunchTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private string CreateScript(string fileName, string content)
    {
        string path = Path.Combine(_tempDir, fileName);
        // chcp 65001: 呼び出し側は StandardOutputEncoding=UTF8 で読むため出力コードページを固定する
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static async Task<(int ExitCode, string Stdout)> RunAsync(ProcessStartInfo psi)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        using IProcessInstance process = new ProcessRunner().Start(psi);
        process.StandardInput.Close();
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        string stdout = await stdoutTask;
        await stderrTask;
        return (process.ExitCode, stdout);
    }

    [Fact]
    public async Task CmdShim_ShouldReceiveMetacharacterArgumentsVerbatim_WithoutInjection()
    {
        // cmd.exe メタ文字（& / %）や日本語・末尾バックスラッシュを含む引数が、
        // cmd.exe ラップを経由しても改変・再解釈されずに届くこと
        string script = CreateScript("dummy-agent.cmd", """
            @echo off
            chcp 65001 >nul
            echo CWD=%CD%
            echo ARG1="%~1"
            echo ARG2="%~2"
            exit /b 0
            """);
        string workDir = Directory.CreateDirectory(Path.Combine(_tempDir, "workspace")).FullName;
        const string metaArg = "thread-owl レビュー 50% & echo pwned";
        const string trailingBackslashArg = @"C:\trailing\";

        ProcessStartInfo psi = AgentProcessStartInfoFactory.Create(script, [metaArg, trailingBackslashArg]);
        psi.WorkingDirectory = workDir;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;

        (int exitCode, string stdout) = await RunAsync(psi);

        exitCode.Should().Be(0);
        stdout.Should().Contain($"CWD={workDir}");
        stdout.Should().Contain($"ARG1=\"{metaArg}\"", "引数が verbatim で届くこと");

        // 末尾バックスラッシュは CommandLineToArgvW 規約（shim が最終的に native 実行形式へ
        // %* を委譲する経路）向けに二重化される。batch の %~2 は引用符除去のみで
        // バックスラッシュ規約を解釈しないため、batch 視点では二重化された形が見える
        stdout.Should().Contain($"ARG2=\"{trailingBackslashArg}\\\"", "末尾バックスラッシュが引用符を破壊しないこと");
        stdout.Split('\n').Select(line => line.Trim()).Should().NotContain("pwned", "& がコマンド区切りとして実行されないこと");
    }

    [Fact]
    public async Task BatShim_ShouldPropagateExitCode()
    {
        string script = CreateScript("failing-agent.bat", """
            @echo off
            exit /b 7
            """);

        ProcessStartInfo psi = AgentProcessStartInfoFactory.Create(script, []);
        psi.WorkingDirectory = _tempDir;

        (int exitCode, _) = await RunAsync(psi);

        exitCode.Should().Be(7);
    }

    [Fact]
    public async Task NativeExecutable_ShouldLaunchDirectly()
    {
        // cmd.exe 自体をネイティブ .exe の代表として直接起動する（ラップなしの経路）
        string comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";

        ProcessStartInfo psi = AgentProcessStartInfoFactory.Create(comSpec, ["/d", "/c", "echo native-ok"]);
        psi.WorkingDirectory = _tempDir;

        (int exitCode, string stdout) = await RunAsync(psi);

        exitCode.Should().Be(0);
        stdout.Should().Contain("native-ok");
    }

    [Fact]
    public async Task ReviewLauncher_ShouldRunDummyAgentInExplicitWorkspace_WhenStartedFromSystem32()
    {
        // タスクスケジューラ起動相当: 親プロセスの cwd が C:\Windows\System32 でも、
        // launcher は継承 cwd を使わず明示的な workspace で dummy agent を実行する（#186 AC）
        string script = CreateScript("dummy-agent.cmd", """
            @echo off
            chcp 65001 >nul
            echo CWD=%CD%
            echo PROMPT="%~2"
            exit /b 0
            """);

        var settingsService = new SettingsService(_tempDir, pnpmBinDir: string.Empty);
        settingsService.UpdateSettings(
            "my-review-cmd", string.Empty,
            "http://localhost:3000", ["queue://review/queue"], 30000,
            script, "-p \"{owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\"",
            script, string.Empty,
            30000,
            "custom", "custom");
        var loggingService = new LoggingService(_tempDir);
        var service = new ReviewLauncherService(settingsService, loggingService, new ProcessRunner());

        var reviewEvent = new ReviewEvent
        {
            EventId = "system32-regression",
            Repository = "scottlz0310/squirrel-notifier",
            PrNumber = 52,
            PrUrl = "https://github.com/scottlz0310/squirrel-notifier/pull/52",
            Source = "queue://review/queue",
            Reason = "opened",
        };

        string originalCwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = Environment.SystemDirectory;
        LauncherResult result;
        try
        {
            result = await service.LaunchAsync(reviewEvent, LauncherRole.Reviewer, CancellationToken.None);
        }
        finally
        {
            Environment.CurrentDirectory = originalCwd;
        }

        string expectedWorkspace = Path.Combine(_tempDir, "launcher-workspace", "reviewer");
        result.Success.Should().BeTrue();
        result.Stdout.Should().Contain($"CWD={expectedWorkspace}");
        result.Stdout.Should().NotContain($"CWD={Environment.SystemDirectory}");
        result.Stdout.Should().Contain(
            "PROMPT=\"scottlz0310/squirrel-notifier#52 を opened モードでレビューしてください\"",
            "プレースホルダー展開済みの日本語プロンプトが cmd.exe ラップを経由しても崩れないこと");
    }
}
