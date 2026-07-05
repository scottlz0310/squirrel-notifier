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

    [Fact]
    public async Task EnqueueAsync_ShouldReturnSuccess_WhenExitCodeIsZero()
    {
        // Arrange
        var reference = new PrReference("scottlz0310", "squirrel-notifier", 123);
        Mock<IProcessInstance> mockProcess = CreateMockProcess(0, "{\"isError\":false,\"content\":[]}", string.Empty);
        ProcessStartInfo? capturedPsi = null;

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Callback<ProcessStartInfo>(psi => capturedPsi = psi)
            .Returns(mockProcess.Object);

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
    public async Task EnqueueAsync_ShouldReturnToolError_WhenExitCodeIsOne()
    {
        // Arrange (allowlist rejection surfaces as exit code 1 / TOOL_ERROR)
        var reference = new PrReference("scottlz0310", "squirrel-notifier", 123);
        string stdout = "{\"isError\":true,\"errorCode\":null,\"content\":[{\"type\":\"text\",\"text\":\"Repository scottlz0310/squirrel-notifier is not in the allowlist\"}]}";
        Mock<IProcessInstance> mockProcess = CreateMockProcess(1, stdout, string.Empty);

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(mockProcess.Object);

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
        Mock<IProcessInstance> mockProcess = CreateMockProcess(2, stdout, string.Empty);

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(mockProcess.Object);

        var service = new EnqueueReviewService(_settingsService, _loggingService, mockRunner.Object);

        // Act
        EnqueueReviewResult result = await service.EnqueueAsync(reference, "opened", CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ExitCode.Should().Be(2);
        result.IsAuthenticationRequired.Should().BeTrue();
        result.ErrorMessage.Should().Contain("認証");
    }

    [Fact]
    public async Task EnqueueAsync_ShouldReturnCommunicationError_WhenExitCodeIsThree()
    {
        // Arrange
        var reference = new PrReference("scottlz0310", "squirrel-notifier", 123);
        Mock<IProcessInstance> mockProcess = CreateMockProcess(3, string.Empty, "fetch failed");

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(mockProcess.Object);

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
    public async Task EnqueueAsync_ShouldSupportCancellation()
    {
        // Arrange
        var reference = new PrReference("scottlz0310", "squirrel-notifier", 123);
        Mock<IProcessInstance> mockProcess = CreateMockProcess(0, "Pending...", string.Empty, delayMs: 5000);

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(mockProcess.Object);

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
        mockProcess.Verify(p => p.Kill(true), Times.Once);
    }
}
