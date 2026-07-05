// <copyright file="ReviewLauncherServiceTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
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

public class ReviewLauncherServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SettingsService _settingsService;
    private readonly LoggingService _loggingService;

    public ReviewLauncherServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ReviewLauncherTests_{Guid.NewGuid()}");
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

    private void ConfigureSettings(
        string reviewerCmd = "reviewer-cmd",
        string reviewerArgs = "--reviewer-arg",
        string reviewedCmd = "reviewed-cmd",
        string reviewedArgs = "--reviewed-arg",
        string role = "reviewer",
        int timeoutMs = 10000)
    {
        _settingsService.UpdateSettings(
            "my-review-cmd", "--repo {owner}/{repo}",
            "http://localhost:3000", new[] { "queue://res" }, 30000,
            reviewerCmd, reviewerArgs,
            reviewedCmd, reviewedArgs,
            role, timeoutMs);
    }

    [Fact]
    public async Task LaunchAsync_ShouldRunSuccessfullyAndReturnExitCodeZero()
    {
        // Arrange
        var reviewEvent = new ReviewEvent
        {
            EventId = "test-event-1",
            Repository = "scottlz0310/squirrel-notifier",
            PrNumber = 52,
            PrUrl = "https://github.com/scottlz0310/squirrel-notifier/pull/52",
            Source = "queue://review/queue",
        };

        ConfigureSettings(reviewerCmd: "launcher-cmd", reviewerArgs: "--launcher-arg");

        Mock<IProcessInstance> mockProcess = CreateMockProcess(0, "Success Output", "");
        ProcessStartInfo? capturedPsi = null;

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Callback<ProcessStartInfo>(psi => capturedPsi = psi)
            .Returns(mockProcess.Object);

        var service = new ReviewLauncherService(_settingsService, _loggingService, mockRunner.Object);

        // Act
        LauncherResult result = await service.LaunchAsync(reviewEvent, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.ExitCode.Should().Be(0);
        result.Stdout.Should().Be("Success Output");
        result.Stderr.Should().BeEmpty();

        capturedPsi.Should().NotBeNull();
        capturedPsi!.FileName.Should().Contain("launcher-cmd");
        capturedPsi.ArgumentList.Should().Contain("--launcher-arg");
        mockProcess.Verify(p => p.Dispose(), Times.Once);
    }

    [Fact]
    public async Task LaunchAsync_ShouldPreventDoubleActivation()
    {
        // Arrange
        var reviewEvent = new ReviewEvent
        {
            EventId = "test-event-2",
            Repository = "scottlz0310/squirrel-notifier",
            PrNumber = 52,
            PrUrl = "https://github.com/scottlz0310/squirrel-notifier/pull/52",
            Source = "queue://review/queue",
        };

        ConfigureSettings();

        Mock<IProcessInstance> mockProcess = CreateMockProcess(0, "Success Output", "", delayMs: 1000);

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Returns(mockProcess.Object);

        var service = new ReviewLauncherService(_settingsService, _loggingService, mockRunner.Object);

        // Act & Assert
        Task<LauncherResult> firstRunTask = service.LaunchAsync(reviewEvent, CancellationToken.None);

        // Wait a small delay to make sure the first run has set _isRunning to true
        await Task.Delay(100);

        LauncherResult secondResult = await service.LaunchAsync(reviewEvent, CancellationToken.None);
        secondResult.Success.Should().BeFalse();
        secondResult.ErrorMessage.Should().Contain("already running");

        LauncherResult firstResult = await firstRunTask;
        firstResult.Success.Should().BeTrue();
    }

    [Fact]
    public async Task LaunchAsync_ShouldSupportCancellation()
    {
        // Arrange
        var reviewEvent = new ReviewEvent
        {
            EventId = "test-event-3",
            Repository = "scottlz0310/squirrel-notifier",
            PrNumber = 52,
            PrUrl = "https://github.com/scottlz0310/squirrel-notifier/pull/52",
            Source = "queue://review/queue",
        };

        ConfigureSettings();

        // Delay mock process to simulate execution time
        Mock<IProcessInstance> mockProcess = CreateMockProcess(0, "Pending...", "", delayMs: 5000);

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Returns(mockProcess.Object);

        var service = new ReviewLauncherService(_settingsService, _loggingService, mockRunner.Object);
        using var cts = new CancellationTokenSource();

        // Act
        Task<LauncherResult> launchTask = service.LaunchAsync(reviewEvent, cts.Token);

        await Task.Delay(100);
        cts.Cancel();

        LauncherResult result = await launchTask;

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("cancelled by user");
        mockProcess.Verify(p => p.Kill(true), Times.Once);
    }

    [Fact]
    public async Task LaunchAsync_ShouldTimeoutProcess()
    {
        // Arrange
        var reviewEvent = new ReviewEvent
        {
            EventId = "test-event-4",
            Repository = "scottlz0310/squirrel-notifier",
            PrNumber = 52,
            PrUrl = "https://github.com/scottlz0310/squirrel-notifier/pull/52",
            Source = "queue://review/queue",
        };

        ConfigureSettings(timeoutMs: 200);

        // Process takes 5000ms
        Mock<IProcessInstance> mockProcess = CreateMockProcess(0, "Slow...", "", delayMs: 5000);

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Returns(mockProcess.Object);

        var service = new ReviewLauncherService(_settingsService, _loggingService, mockRunner.Object);

        // Act
        LauncherResult result = await service.LaunchAsync(reviewEvent, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("timed out");
        mockProcess.Verify(p => p.Kill(true), Times.Once);
    }

    [Theory]
    [InlineData("queue://review/re-review-requests", "reviewer", "reviewer-cmd")]
    [InlineData("queue://review/queue", "reviewer", "reviewer-cmd")]
    [InlineData("queue://review/queue", "reviewed", "reviewed-cmd")]
    public async Task LaunchAsync_ShouldSelectCorrectSlotBySourceAndRole(
        string source, string role, string expectedCmd)
    {
        // Arrange
        var reviewEvent = new ReviewEvent
        {
            EventId = "test-slot",
            Repository = "scottlz0310/squirrel-notifier",
            PrNumber = 52,
            PrUrl = "https://github.com/scottlz0310/squirrel-notifier/pull/52",
            Source = source,
        };

        ConfigureSettings(
            reviewerCmd: "reviewer-cmd", reviewerArgs: "--reviewer-arg",
            reviewedCmd: "reviewed-cmd", reviewedArgs: "--reviewed-arg",
            role: role);

        Mock<IProcessInstance> mockProcess = CreateMockProcess(0, string.Empty, string.Empty);
        ProcessStartInfo? capturedPsi = null;

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Callback<ProcessStartInfo>(psi => capturedPsi = psi)
            .Returns(mockProcess.Object);

        var service = new ReviewLauncherService(_settingsService, _loggingService, mockRunner.Object);

        // Act
        await service.LaunchAsync(reviewEvent, CancellationToken.None);

        // Assert
        capturedPsi.Should().NotBeNull();
        capturedPsi!.FileName.Should().Contain(expectedCmd);
    }

    [Theory]
    [InlineData("queue://review/re-review-requests", "reviewer", "reviewer-cmd", "--reviewer-arg")]
    [InlineData("queue://review/queue", "reviewer", "reviewer-cmd", "--reviewer-arg")]
    [InlineData("queue://review/queue", "reviewed", "reviewed-cmd", "--reviewed-arg")]
    public void BuildCommandLine_ShouldSelectCorrectSlotBySourceAndRole(
        string source, string role, string expectedCmd, string expectedArg)
    {
        // Arrange
        var reviewEvent = new ReviewEvent
        {
            EventId = "test-copy",
            Repository = "scottlz0310/squirrel-notifier",
            PrNumber = 52,
            PrUrl = "https://github.com/scottlz0310/squirrel-notifier/pull/52",
            Source = source,
        };

        ConfigureSettings(
            reviewerCmd: "reviewer-cmd", reviewerArgs: "--reviewer-arg",
            reviewedCmd: "reviewed-cmd", reviewedArgs: "--reviewed-arg",
            role: role);

        var mockRunner = new Mock<IProcessRunner>();
        var service = new ReviewLauncherService(_settingsService, _loggingService, mockRunner.Object);

        // Act
        string commandLine = service.BuildCommandLine(reviewEvent);

        // Assert
        commandLine.Should().Be($"{expectedCmd} {expectedArg}");
        mockRunner.Verify(r => r.Start(It.IsAny<ProcessStartInfo>()), Times.Never);
    }

    [Fact]
    public void BuildCommandLine_ShouldExpandPlaceholdersAndQuoteArgumentsWithSpaces()
    {
        // Arrange: 既定のテンプレートは -p "<prompt>" 形式で、prompt にプレースホルダーを含む
        var reviewEvent = new ReviewEvent
        {
            EventId = "test-copy-2",
            Repository = "scottlz0310/squirrel-notifier",
            PrNumber = 123,
            PrUrl = "https://github.com/scottlz0310/squirrel-notifier/pull/123",
            Source = "queue://review/queue",
            Reason = "opened",
        };

        ConfigureSettings(
            reviewerCmd: "claude",
            reviewerArgs: "-p \"/thread-owl-pr-reviewer {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\"",
            role: "reviewer");

        var mockRunner = new Mock<IProcessRunner>();
        var service = new ReviewLauncherService(_settingsService, _loggingService, mockRunner.Object);

        // Act
        string commandLine = service.BuildCommandLine(reviewEvent);

        // Assert
        commandLine.Should().Be("claude -p \"/thread-owl-pr-reviewer scottlz0310/squirrel-notifier#123 を opened モードでレビューしてください\"");
    }
}
