// <copyright file="GhApiClientTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Moq;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class GhApiClientTests
{
    private const string _ghPath = @"C:\tools\gh.exe";

    [Theory]
    [InlineData(false, new[] { "api", "repos/o/r/pulls/1", "--jq", ".x" })]
    [InlineData(true, new[] { "api", "repos/o/r/pulls/1", "--paginate", "--jq", ".x" })]
    public async Task GetAsync_ShouldRunGhApiWithJq_AndReturnOutput(bool paginate, string[] expectedArguments)
    {
        ProcessStartInfo? captured = null;
        Mock<IProcessRunner> runner = CreateRunner(CreateProcess(0, "{\"ok\":true}", string.Empty), psi => captured = psi);

        GhApiResult result = await CreateClient(runner).GetAsync("repos/o/r/pulls/1", ".x", paginate, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Output.Should().Be("{\"ok\":true}");
        result.HttpStatus.Should().BeNull();
        captured.Should().NotBeNull();
        captured!.FileName.Should().Be(_ghPath);
        captured.ArgumentList.Should().Equal(expectedArguments);
    }

    // 無人で動くため、対話プロンプトと更新通知でブロックしない。引数はシェルを介さず ArgumentList で渡す
    [Fact]
    public async Task GetAsync_ShouldRunWithoutShellAndDisablePrompts()
    {
        ProcessStartInfo? captured = null;
        Mock<IProcessRunner> runner = CreateRunner(CreateProcess(0, string.Empty, string.Empty), psi => captured = psi);

        await CreateClient(runner).GetAsync("repos/o/r/pulls/1", ".x", paginate: false, CancellationToken.None);

        captured!.UseShellExecute.Should().BeFalse();
        captured.CreateNoWindow.Should().BeTrue();
        captured.Environment["GH_PROMPT_DISABLED"].Should().Be("1");
        captured.Environment["GH_NO_UPDATE_NOTIFIER"].Should().Be("1");
    }

    [Theory]
    [InlineData("gh: Not Found (HTTP 404)", 404)]
    [InlineData("gh: Forbidden (HTTP 403)", 403)]
    [InlineData("gh: Branch not protected (HTTP 404)\r\n", 404)]
    [InlineData("gh: authentication required", null)]
    [InlineData("", null)]
    public async Task GetAsync_ShouldReturnFailureWithHttpStatus_WhenGhExitsNonZero(string stderr, int? expectedStatus)
    {
        Mock<IProcessRunner> runner = CreateRunner(CreateProcess(1, "{\"message\":\"Not Found\"}", stderr));

        GhApiResult result = await CreateClient(runner).GetAsync("repos/o/r/pulls/1", ".x", paginate: false, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Output.Should().BeEmpty();
        result.HttpStatus.Should().Be(expectedStatus);
        result.Error.Should().NotBeNullOrWhiteSpace();
        if (stderr.Length > 0)
        {
            result.Error.Should().Contain(stderr.Trim());
        }
        else
        {
            result.Error.Should().Contain("終了コード 1");
        }
    }

    [Fact]
    public async Task GetAsync_ShouldMaskTokensInError()
    {
        const string token = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";
        Mock<IProcessRunner> runner = CreateRunner(CreateProcess(1, string.Empty, $"gh: Bad credentials {token} (HTTP 401)"));

        GhApiResult result = await CreateClient(runner).GetAsync("repos/o/r/pulls/1", ".x", paginate: false, CancellationToken.None);

        result.HttpStatus.Should().Be(401);
        result.Error.Should().NotContain(token).And.Contain("***");
    }

    [Fact]
    public async Task GetAsync_ShouldReturnFailureWithoutStartingProcess_WhenGhIsNotFound()
    {
        Mock<IProcessRunner> runner = new();
        GhApiClient client = new(runner.Object, _ => null);

        GhApiResult result = await client.GetAsync("repos/o/r/pulls/1", ".x", paginate: false, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("gh コマンドが見つかりません");
        runner.Verify(r => r.Start(It.IsAny<ProcessStartInfo>()), Times.Never);
    }

    [Fact]
    public async Task GetAsync_ShouldReturnFailure_WhenProcessCannotBeStarted()
    {
        Mock<IProcessRunner> runner = new();
        runner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Throws(new System.ComponentModel.Win32Exception("指定されたファイルが見つかりません。"));

        GhApiResult result = await CreateClient(runner).GetAsync("repos/o/r/pulls/1", ".x", paginate: false, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.HttpStatus.Should().BeNull();
        result.Error.Should().Contain("gh を実行できません").And.Contain("指定されたファイルが見つかりません");
    }

    [Fact]
    public async Task GetAsync_ShouldKillProcessAndReturnFailure_WhenTimedOut()
    {
        Mock<IProcessInstance> process = CreateProcess(0, string.Empty, string.Empty);
        process.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken token) => Task.Delay(Timeout.Infinite, token));
        Mock<IProcessRunner> runner = CreateRunner(process);
        GhApiClient client = new(runner.Object, _ => _ghPath, timeout: TimeSpan.FromMilliseconds(50));

        GhApiResult result = await client.GetAsync("repos/o/r/pulls/1", ".x", paginate: false, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.HttpStatus.Should().BeNull();
        result.Error.Should().Contain("タイムアウト");
        process.Verify(p => p.Kill(true), Times.Once);
        process.Verify(p => p.Dispose(), Times.Once);
    }

    // 呼び出し側のキャンセルは失敗として返さず、伝播する（タイムアウトと区別する）
    [Fact]
    public async Task GetAsync_ShouldKillProcessAndPropagate_WhenCancelledByCaller()
    {
        Mock<IProcessInstance> process = CreateProcess(0, string.Empty, string.Empty);
        process.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken token) => Task.Delay(Timeout.Infinite, token));
        Mock<IProcessRunner> runner = CreateRunner(process);
        using CancellationTokenSource cts = new();

        Task<GhApiResult> task = CreateClient(runner).GetAsync("repos/o/r/pulls/1", ".x", paginate: false, cts.Token);
        await cts.CancelAsync();

        await task.Awaiting(t => t).Should().ThrowAsync<OperationCanceledException>();
        process.Verify(p => p.Kill(true), Times.Once);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task GetAsync_ShouldRejectBlankArguments(string value)
    {
        Mock<IProcessRunner> runner = new();
        GhApiClient client = CreateClient(runner);

        Func<Task> pathAct = () => client.GetAsync(value, ".x", paginate: false, CancellationToken.None);
        Func<Task> jqAct = () => client.GetAsync("repos/o/r/pulls/1", value, paginate: false, CancellationToken.None);

        await pathAct.Should().ThrowAsync<ArgumentException>();
        await jqAct.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_ShouldRejectNonPositiveTimeout(int seconds)
    {
        Action act = () => _ = new GhApiClient(timeout: TimeSpan.FromSeconds(seconds));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static GhApiClient CreateClient(Mock<IProcessRunner> runner)
        => new(runner.Object, _ => _ghPath);

    private static Mock<IProcessRunner> CreateRunner(Mock<IProcessInstance> process, Action<ProcessStartInfo>? capture = null)
    {
        Mock<IProcessRunner> runner = new();
        Moq.Language.Flow.ISetup<IProcessRunner, IProcessInstance> setup = runner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()));
        if (capture is not null)
        {
            setup.Callback<ProcessStartInfo>(capture);
        }

        setup.Returns(process.Object);
        return runner;
    }

    private static Mock<IProcessInstance> CreateProcess(int exitCode, string stdout, string stderr)
    {
        Mock<IProcessInstance> process = new();
        process.SetupGet(p => p.ExitCode).Returns(exitCode);
        process.SetupGet(p => p.StandardOutput).Returns(new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(stdout))));
        process.SetupGet(p => p.StandardError).Returns(new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(stderr))));
        process.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return process;
    }
}
