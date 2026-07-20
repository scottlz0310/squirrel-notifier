// <copyright file="McpLoginServiceTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class McpLoginServiceTests : IDisposable
{
    private const string _successStdout =
        "user-code WDJB-MJHT\n" +
        "verification-uri https://gateway.example/device\n" +
        "verification-uri-complete https://gateway.example/device?user_code=WDJB-MJHT\n" +
        "login-status success\n" +
        "token-origin https://gateway.example\n" +
        "token-expires-at 2026-07-21T01:00:00.000Z\n";

    private readonly string _tempDir;
    private readonly SettingsService _settingsService;
    private readonly LoggingService _loggingService;

    public McpLoginServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"McpLoginTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _settingsService = new SettingsService(_tempDir, pnpmBinDir: string.Empty);
        _loggingService = new LoggingService(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private static Mock<IProcessInstance> CreateMockProcess(int exitCode, string stdout, string stderr, int waitDelayMs = 0)
    {
        var mockProcess = new Mock<IProcessInstance>();
        mockProcess.SetupGet(p => p.ExitCode).Returns(exitCode);
        mockProcess.SetupGet(p => p.StandardOutput).Returns(new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(stdout))));
        mockProcess.SetupGet(p => p.StandardError).Returns(new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(stderr))));

        if (waitDelayMs > 0)
        {
            mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>()))
                .Returns(async (CancellationToken t) => await Task.Delay(waitDelayMs, t));
        }
        else
        {
            mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }

        return mockProcess;
    }

    private static Mock<IProcessInstance> CreateVersionOkMockProcess()
    {
        return CreateMockProcess(0, "mcp-resource-subscriber v0.5.0", string.Empty);
    }

    private static Mock<IProcessRunner> CreateRunner(Mock<IProcessInstance> loginProcess, Mock<IProcessInstance> versionProcess)
    {
        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.Is<ProcessStartInfo>(p => p.ArgumentList.Contains("--version"))))
            .Returns(versionProcess.Object);
        mockRunner.Setup(r => r.Start(It.Is<ProcessStartInfo>(p => p.ArgumentList.Contains("--login"))))
            .Returns(loginProcess.Object);
        return mockRunner;
    }

    private sealed class FakeUrlOpener : IUrlOpener
    {
        private readonly bool _result;

        public FakeUrlOpener(bool result)
        {
            _result = result;
        }

        public List<string> OpenedUrls { get; } = new();

        public bool TryOpen(string url)
        {
            OpenedUrls.Add(url);
            return _result;
        }
    }

    [Fact]
    public async Task LoginAsync_ShouldSucceed_AndOpenBrowser_WhenLoginStatusIsSuccess()
    {
        Mock<IProcessInstance> loginProcess = CreateMockProcess(0, _successStdout, string.Empty);
        Mock<IProcessInstance> versionProcess = CreateVersionOkMockProcess();
        Mock<IProcessRunner> runner = CreateRunner(loginProcess, versionProcess);
        var urlOpener = new FakeUrlOpener(result: true);

        var service = new McpLoginService(_settingsService, _loggingService, runner.Object, urlOpener);
        DeviceVerificationInfo? lastInfo = null;
        service.VerificationReceived += (_, info) => lastInfo = info;

        McpLoginResult result = await service.LoginAsync(CancellationToken.None);

        result.Outcome.Should().Be(McpLoginOutcome.Succeeded);
        result.Success.Should().BeTrue();

        // ブラウザは verification-uri（base）で一度だけ開く。
        urlOpener.OpenedUrls.Should().ContainSingle().Which.Should().Be("https://gateway.example/device");

        // UI へ渡す最新情報は complete URI と code を含む。
        lastInfo.Should().NotBeNull();
        lastInfo!.UserCode.Should().Be("WDJB-MJHT");
        lastInfo.DisplayUri.Should().Be("https://gateway.example/device?user_code=WDJB-MJHT");
        lastInfo.BrowserOpened.Should().BeTrue();
    }

    [Fact]
    public async Task LoginAsync_ShouldStillSucceed_WhenBrowserFailsToOpen()
    {
        Mock<IProcessInstance> loginProcess = CreateMockProcess(0, _successStdout, string.Empty);
        Mock<IProcessInstance> versionProcess = CreateVersionOkMockProcess();
        Mock<IProcessRunner> runner = CreateRunner(loginProcess, versionProcess);
        var urlOpener = new FakeUrlOpener(result: false);

        var service = new McpLoginService(_settingsService, _loggingService, runner.Object, urlOpener);
        DeviceVerificationInfo? firstInfo = null;
        service.VerificationReceived += (_, info) => firstInfo ??= info;

        McpLoginResult result = await service.LoginAsync(CancellationToken.None);

        result.Outcome.Should().Be(McpLoginOutcome.Succeeded);

        // ブラウザ起動に失敗しても認証は継続し、UI へ URL / code を提示できる。
        urlOpener.OpenedUrls.Should().ContainSingle();
        firstInfo.Should().NotBeNull();
        firstInfo!.BrowserOpened.Should().BeFalse();
        firstInfo.VerificationUri.Should().Be("https://gateway.example/device");
        firstInfo.UserCode.Should().Be("WDJB-MJHT");
    }

    [Fact]
    public async Task LoginAsync_ShouldReturnFailed_WhenLoginStatusIsFailed()
    {
        string stdout = "login-status failed\n";
        string stderr = "login failed: device flow was denied by the user\n";
        Mock<IProcessInstance> loginProcess = CreateMockProcess(1, stdout, stderr);
        Mock<IProcessInstance> versionProcess = CreateVersionOkMockProcess();
        Mock<IProcessRunner> runner = CreateRunner(loginProcess, versionProcess);

        var service = new McpLoginService(_settingsService, _loggingService, runner.Object, new FakeUrlOpener(true));

        McpLoginResult result = await service.LoginAsync(CancellationToken.None);

        result.Outcome.Should().Be(McpLoginOutcome.Failed);
        result.ErrorMessage.Should().Contain("device flow was denied by the user");
    }

    [Fact]
    public async Task LoginAsync_ShouldReturnGatewayGuidance_WhenServerUrlUnknown()
    {
        string stdout = "login-status failed\nerror-code SERVER_URL_UNKNOWN\n";
        Mock<IProcessInstance> loginProcess = CreateMockProcess(1, stdout, string.Empty);
        Mock<IProcessInstance> versionProcess = CreateVersionOkMockProcess();
        Mock<IProcessRunner> runner = CreateRunner(loginProcess, versionProcess);

        var service = new McpLoginService(_settingsService, _loggingService, runner.Object, new FakeUrlOpener(true));

        McpLoginResult result = await service.LoginAsync(CancellationToken.None);

        result.Outcome.Should().Be(McpLoginOutcome.Failed);
        result.ErrorCode.Should().Be("SERVER_URL_UNKNOWN");
        result.ErrorMessage.Should().Contain("Gateway URL");
    }

    [Fact]
    public async Task LoginAsync_ShouldFailFast_WhenSubscriberVersionIsTooOld()
    {
        Mock<IProcessInstance> versionProcess = CreateMockProcess(0, "mcp-resource-subscriber v0.2.0", string.Empty);
        Mock<IProcessInstance> loginProcess = CreateMockProcess(0, _successStdout, string.Empty);
        Mock<IProcessRunner> runner = CreateRunner(loginProcess, versionProcess);
        var urlOpener = new FakeUrlOpener(true);

        var service = new McpLoginService(_settingsService, _loggingService, runner.Object, urlOpener);

        McpLoginResult result = await service.LoginAsync(CancellationToken.None);

        result.Outcome.Should().Be(McpLoginOutcome.Failed);
        result.ErrorMessage.Should().Contain("v0.2.0");
        result.ErrorMessage.Should().Contain("v0.3.0");
        runner.Verify(r => r.Start(It.Is<ProcessStartInfo>(p => p.ArgumentList.Contains("--login"))), Times.Never);
        urlOpener.OpenedUrls.Should().BeEmpty();
    }

    [Fact]
    public async Task LoginAsync_ShouldReturnCancelled_WhenTokenIsCancelled()
    {
        Mock<IProcessInstance> loginProcess = CreateMockProcess(0, _successStdout, string.Empty, waitDelayMs: 5000);
        Mock<IProcessInstance> versionProcess = CreateVersionOkMockProcess();
        Mock<IProcessRunner> runner = CreateRunner(loginProcess, versionProcess);

        var service = new McpLoginService(_settingsService, _loggingService, runner.Object, new FakeUrlOpener(true));
        using var cts = new CancellationTokenSource();

        Task<McpLoginResult> task = service.LoginAsync(cts.Token);
        await Task.Delay(100);
        cts.Cancel();
        McpLoginResult result = await task;

        result.Outcome.Should().Be(McpLoginOutcome.Cancelled);
        loginProcess.Verify(p => p.Kill(true), Times.Once);
    }

    [Fact]
    public async Task LoginAsync_ShouldReturnTimedOut_WhenApprovalExceedsTimeout()
    {
        Mock<IProcessInstance> loginProcess = CreateMockProcess(0, _successStdout, string.Empty, waitDelayMs: 5000);
        Mock<IProcessInstance> versionProcess = CreateVersionOkMockProcess();
        Mock<IProcessRunner> runner = CreateRunner(loginProcess, versionProcess);

        // 承認待ちの timeout を極小値に設定し、user cancel と区別されることを検証する。
        var service = new McpLoginService(_settingsService, _loggingService, runner.Object, new FakeUrlOpener(true), loginTimeoutMs: 150);

        McpLoginResult result = await service.LoginAsync(CancellationToken.None);

        result.Outcome.Should().Be(McpLoginOutcome.TimedOut);
        loginProcess.Verify(p => p.Kill(true), Times.Once);
    }
}
