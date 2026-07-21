// <copyright file="EnqueueReviewServiceTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class EnqueueReviewServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SettingsService _settingsService;
    private readonly LoggingService _loggingService;

    public EnqueueReviewServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"EnqueueReviewTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _settingsService = new SettingsService(_tempDir);
        _loggingService = new LoggingService(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private Mock<IProcessInstance> CreateMockProcess(int exitCode, string stdout, string stderr, int delayMs = 0)
    {
        var mockProcess = new Mock<IProcessInstance>();
        mockProcess.SetupGet(p => p.ExitCode).Returns(exitCode);
        mockProcess.SetupGet(p => p.StandardOutput).Returns(new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(stdout))));
        mockProcess.SetupGet(p => p.StandardError).Returns(new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(stderr))));

        if (delayMs > 0)
        {
            mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>()))
                .Returns(async (CancellationToken t) => await Task.Delay(delayMs, t));
        }
        else
        {
            mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }

        return mockProcess;
    }

    // --version の既定モック（現行の最低要求バージョンを満たす）。call モード呼び出し前に
    // EnqueueReviewService が必ず 1 回 --version を実行するため、全テストで必要。
    private Mock<IProcessInstance> CreateVersionOkMockProcess()
    {
        return CreateMockProcess(0, "mcp-resource-subscriber v0.4.0", string.Empty);
    }

    private static Mock<IProcessRunner> CreateRunner(
        Mock<IProcessInstance> callProcess,
        Mock<IProcessInstance> versionProcess,
        Action<ProcessStartInfo>? captureCallPsi = null)
    {
        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.Is<ProcessStartInfo>(p => p.ArgumentList.Contains("--version"))))
            .Returns(versionProcess.Object);

        Moq.Language.Flow.ISetup<IProcessRunner, IProcessInstance> callSetup =
            mockRunner.Setup(r => r.Start(It.Is<ProcessStartInfo>(p => p.ArgumentList.Contains("call"))));

        if (captureCallPsi != null)
        {
            callSetup.Callback<ProcessStartInfo>(captureCallPsi);
        }

        callSetup.Returns(callProcess.Object);

        return mockRunner;
    }

    [Fact]
    public async Task EnqueueAsync_ShouldReturnSuccess_WhenExitCodeIsZero()
    {
        // Arrange
        var reference = new PrReference("scottlz0310", "squirrel-notifier", 123);
        Mock<IProcessInstance> callProcess = CreateMockProcess(0, "{\"isError\":false,\"content\":[]}", string.Empty);
        Mock<IProcessInstance> versionProcess = CreateVersionOkMockProcess();
        ProcessStartInfo? capturedPsi = null;

        Mock<IProcessRunner> mockRunner = CreateRunner(callProcess, versionProcess, psi => capturedPsi = psi);

        var service = new EnqueueReviewService(_settingsService, _loggingService, mockRunner.Object);

        // Act
        EnqueueReviewResult result = await service.EnqueueAsync(reference, "opened", CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.ExitCode.Should().Be(0);

        capturedPsi.Should().NotBeNull();
        capturedPsi!.ArgumentList.Should().Contain("call");
        capturedPsi.ArgumentList.Should().Contain("--tool");
        capturedPsi.ArgumentList.Should().Contain("enqueue_review");
        capturedPsi.ArgumentList[0].Should().Be("call"); // "call" は先頭 positional 引数である必要がある

        int argsIndex = capturedPsi.ArgumentList.IndexOf("--args");
        argsIndex.Should().BeGreaterThan(-1);
        string argsJson = capturedPsi.ArgumentList[argsIndex + 1];
        using JsonDocument doc = JsonDocument.Parse(argsJson);
        doc.RootElement.GetProperty("owner").GetString().Should().Be("scottlz0310");
        doc.RootElement.GetProperty("repo").GetString().Should().Be("squirrel-notifier");
        doc.RootElement.GetProperty("prNumber").GetInt32().Should().Be(123);
        doc.RootElement.GetProperty("reason").GetString().Should().Be("opened");
    }

    [Fact]
    public async Task EnqueueAsync_ShouldIncludeSubscriberArguments_BeforeUrlFlag()
    {
        // Arrange: 購読側と同じ固定引数（例: --skip-resource-list-check）を call モードにも引き継ぐ
        var reference = new PrReference("scottlz0310", "squirrel-notifier", 123);
        _settingsService.UpdateSettings(
            "mcp-resource-subscriber", "--skip-resource-list-check",
            "http://localhost:3000", new[] { "queue://review/queue" }, 30000,
            "claude", "-p test", "claude", "-p test", 300000,
            "custom", "custom");

        Mock<IProcessInstance> callProcess = CreateMockProcess(0, "{\"isError\":false,\"content\":[]}", string.Empty);
        Mock<IProcessInstance> versionProcess = CreateVersionOkMockProcess();
        ProcessStartInfo? capturedPsi = null;

        Mock<IProcessRunner> mockRunner = CreateRunner(callProcess, versionProcess, psi => capturedPsi = psi);

        var service = new EnqueueReviewService(_settingsService, _loggingService, mockRunner.Object);

        // Act
        await service.EnqueueAsync(reference, "opened", CancellationToken.None);

        // Assert
        capturedPsi.Should().NotBeNull();
        capturedPsi!.ArgumentList[0].Should().Be("call");
        capturedPsi.ArgumentList.Should().Contain("--skip-resource-list-check");
        int flagIndex = capturedPsi.ArgumentList.IndexOf("--skip-resource-list-check");
        int urlIndex = capturedPsi.ArgumentList.IndexOf("--url");
        flagIndex.Should().BeLessThan(urlIndex);
    }

    [Fact]
    public async Task EnqueueAsync_ShouldReturnToolError_WhenExitCodeIsOne()
    {
        // Arrange (allowlist rejection surfaces as exit code 1 / TOOL_ERROR)
        var reference = new PrReference("scottlz0310", "squirrel-notifier", 123);
        string stdout = "{\"isError\":true,\"errorCode\":null,\"content\":[{\"type\":\"text\",\"text\":\"Repository scottlz0310/squirrel-notifier is not in the allowlist\"}]}";
        Mock<IProcessInstance> callProcess = CreateMockProcess(1, stdout, string.Empty);
        Mock<IProcessInstance> versionProcess = CreateVersionOkMockProcess();

        Mock<IProcessRunner> mockRunner = CreateRunner(callProcess, versionProcess);

        var service = new EnqueueReviewService(_settingsService, _loggingService, mockRunner.Object);

        // Act
        EnqueueReviewResult result = await service.EnqueueAsync(reference, "opened", CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ExitCode.Should().Be(1);
        result.IsAuthenticationRequired.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not in the allowlist");
    }

    [Fact]
    public async Task EnqueueAsync_ShouldReturnAuthError_WhenExitCodeIsTwo()
    {
        // Arrange
        var reference = new PrReference("scottlz0310", "squirrel-notifier", 123);
        string stdout = "{\"isError\":true,\"errorCode\":\"AUTH_LOGIN_REQUIRED\",\"content\":null}";
        Mock<IProcessInstance> callProcess = CreateMockProcess(2, stdout, string.Empty);
        Mock<IProcessInstance> versionProcess = CreateVersionOkMockProcess();

        Mock<IProcessRunner> mockRunner = CreateRunner(callProcess, versionProcess);

        var service = new EnqueueReviewService(_settingsService, _loggingService, mockRunner.Object);

        // Act
        EnqueueReviewResult result = await service.EnqueueAsync(reference, "opened", CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ExitCode.Should().Be(2);
        result.IsAuthenticationRequired.Should().BeTrue();
        result.ErrorMessage.Should().Contain("--login");
    }

    [Fact]
    public async Task EnqueueAsync_ShouldReturnAuthFailedGuidance_WhenErrorCodeIsAuthFailed()
    {
        // Arrange: AUTH_FAILED（明示指定した MCP_PROBE_AUTH_TOKEN が無効等）は --login では解消しない
        var reference = new PrReference("scottlz0310", "squirrel-notifier", 123);
        string stdout = "{\"isError\":true,\"errorCode\":\"AUTH_FAILED\",\"content\":[{\"type\":\"text\",\"text\":\"invalid_token\"}]}";
        Mock<IProcessInstance> callProcess = CreateMockProcess(2, stdout, string.Empty);
        Mock<IProcessInstance> versionProcess = CreateVersionOkMockProcess();

        Mock<IProcessRunner> mockRunner = CreateRunner(callProcess, versionProcess);

        var service = new EnqueueReviewService(_settingsService, _loggingService, mockRunner.Object);

        // Act
        EnqueueReviewResult result = await service.EnqueueAsync(reference, "opened", CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.IsAuthenticationRequired.Should().BeTrue();
        result.ErrorMessage.Should().Contain("MCP_PROBE_AUTH_TOKEN");
        result.ErrorMessage.Should().NotContain("を実行して再認証してください");
    }

    [Fact]
    public async Task EnqueueAsync_ShouldReturnCommunicationError_WhenExitCodeIsThree()
    {
        // Arrange
        var reference = new PrReference("scottlz0310", "squirrel-notifier", 123);
        Mock<IProcessInstance> callProcess = CreateMockProcess(3, string.Empty, "fetch failed");
        Mock<IProcessInstance> versionProcess = CreateVersionOkMockProcess();

        Mock<IProcessRunner> mockRunner = CreateRunner(callProcess, versionProcess);

        var service = new EnqueueReviewService(_settingsService, _loggingService, mockRunner.Object);

        // Act
        EnqueueReviewResult result = await service.EnqueueAsync(reference, "opened", CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ExitCode.Should().Be(3);
        result.IsAuthenticationRequired.Should().BeFalse();
        result.ErrorMessage.Should().Contain("通信エラー");
    }

    [Fact]
    public async Task EnqueueAsync_ShouldFailFast_WhenSubscriberVersionIsTooOld()
    {
        // Arrange: v0.3.0 は call サブコマンドを認識せず subscribe モードへフォールバックするため、
        // 実際に call を試みる前に検出して拒否する。
        var reference = new PrReference("scottlz0310", "squirrel-notifier", 123);
        Mock<IProcessInstance> versionProcess = CreateMockProcess(0, "mcp-resource-subscriber v0.3.0", string.Empty);
        Mock<IProcessInstance> callProcess = CreateMockProcess(0, "{\"isError\":false,\"content\":[]}", string.Empty);

        Mock<IProcessRunner> mockRunner = CreateRunner(callProcess, versionProcess);

        var service = new EnqueueReviewService(_settingsService, _loggingService, mockRunner.Object);

        // Act
        EnqueueReviewResult result = await service.EnqueueAsync(reference, "opened", CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("v0.3.0");
        result.ErrorMessage.Should().Contain("v0.4.0");
        mockRunner.Verify(r => r.Start(It.Is<ProcessStartInfo>(p => p.ArgumentList.Contains("call"))), Times.Never);
    }

    [Fact]
    public async Task EnqueueAsync_ShouldFailFast_WhenSubscriberVersionOutputIsUnparseable()
    {
        // Arrange
        var reference = new PrReference("scottlz0310", "squirrel-notifier", 123);
        Mock<IProcessInstance> versionProcess = CreateMockProcess(0, "unexpected output", string.Empty);
        Mock<IProcessInstance> callProcess = CreateMockProcess(0, "{\"isError\":false,\"content\":[]}", string.Empty);

        Mock<IProcessRunner> mockRunner = CreateRunner(callProcess, versionProcess);

        var service = new EnqueueReviewService(_settingsService, _loggingService, mockRunner.Object);

        // Act
        EnqueueReviewResult result = await service.EnqueueAsync(reference, "opened", CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("バージョンを確認できません");
        mockRunner.Verify(r => r.Start(It.Is<ProcessStartInfo>(p => p.ArgumentList.Contains("call"))), Times.Never);
    }

    [Fact]
    public async Task EnqueueAsync_ShouldDrainVersionCheckStderr_WhenSubscriberFillsThePipeBuffer()
    {
        // --version の stderr を読み進めない実装では、パイプバッファを超えた時点で
        // 子プロセスが書き込みでブロックし WaitForExitAsync が返らなくなる（#201）。
        // この double は stderr が読み切られるまで終了しないため、stderr 未読なら
        // call timeout に達して失敗する。
        var reference = new PrReference("scottlz0310", "squirrel-notifier", 123);
        Mock<IProcessInstance> versionProcess =
            StderrDrainProcessDouble.CreateBlockingOnStderr("mcp-resource-subscriber v0.4.0");
        Mock<IProcessInstance> callProcess = CreateMockProcess(0, "{\"isError\":false,\"content\":[]}", string.Empty);

        Mock<IProcessRunner> mockRunner = CreateRunner(callProcess, versionProcess);

        var service = new EnqueueReviewService(_settingsService, _loggingService, mockRunner.Object);

        // Act
        EnqueueReviewResult result = await service.EnqueueAsync(reference, "opened", CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        mockRunner.Verify(r => r.Start(It.Is<ProcessStartInfo>(p => p.ArgumentList.Contains("call"))), Times.Once);
    }

    [Fact]
    public async Task EnqueueAsync_ShouldIncludeMaskedStderr_WhenVersionCannotBeDetermined()
    {
        // Arrange
        const string secret = "super-secret-token-value";
        var reference = new PrReference("scottlz0310", "squirrel-notifier", 123);
        Mock<IProcessInstance> versionProcess = CreateMockProcess(
            1, string.Empty, $"failed to read config: MCP_PROBE_AUTH_TOKEN={secret}");
        Mock<IProcessInstance> callProcess = CreateMockProcess(0, "{\"isError\":false,\"content\":[]}", string.Empty);

        Mock<IProcessRunner> mockRunner = CreateRunner(callProcess, versionProcess);

        var service = new EnqueueReviewService(
            _settingsService, _loggingService, mockRunner.Object, new SecretMasker([secret]));

        // Act
        EnqueueReviewResult result = await service.EnqueueAsync(reference, "opened", CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("failed to read config");
        result.ErrorMessage.Should().NotContain(secret);
        result.ErrorMessage.Should().Contain("***");
        mockRunner.Verify(r => r.Start(It.Is<ProcessStartInfo>(p => p.ArgumentList.Contains("call"))), Times.Never);
    }

    [Fact]
    public async Task EnqueueAsync_ShouldSupportCancellation()
    {
        // Arrange
        var reference = new PrReference("scottlz0310", "squirrel-notifier", 123);
        Mock<IProcessInstance> callProcess = CreateMockProcess(0, "Pending...", string.Empty, delayMs: 5000);
        Mock<IProcessInstance> versionProcess = CreateVersionOkMockProcess();

        Mock<IProcessRunner> mockRunner = CreateRunner(callProcess, versionProcess);

        var service = new EnqueueReviewService(_settingsService, _loggingService, mockRunner.Object);
        using var cts = new CancellationTokenSource();

        // Act
        Task<EnqueueReviewResult> task = service.EnqueueAsync(reference, "opened", cts.Token);
        await Task.Delay(100);
        cts.Cancel();
        EnqueueReviewResult result = await task;

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("キャンセル");
        callProcess.Verify(p => p.Kill(true), Times.Once);
    }
}
