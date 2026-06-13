// <copyright file="McpSubscriptionServiceTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

#pragma warning disable IDE0008


using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Moq;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class McpSubscriptionServiceTests : IDisposable
{
    private readonly string _settingsDirectory;
    private readonly SettingsService _settingsService;
    private readonly NotificationService _notificationService;
    private readonly LoggingService _loggingService;

    public McpSubscriptionServiceTests()
    {
        _settingsDirectory = Path.Combine(Path.GetTempPath(), $"McpSubscriptionServiceTests_{Guid.NewGuid()}");
        _settingsService = new SettingsService(_settingsDirectory);
        _notificationService = new NotificationService();
        _loggingService = new LoggingService(_settingsDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_settingsDirectory))
        {
            Directory.Delete(_settingsDirectory, true);
        }
    }

    private static IProcessInstance CreateMockProcess(int exitCode, string stdout, string stderr)
    {
        byte[] stdoutBytes = Encoding.UTF8.GetBytes(stdout);
        var stdoutStream = new MemoryStream(stdoutBytes);
        var stdoutReader = new StreamReader(stdoutStream);

        byte[] stderrBytes = Encoding.UTF8.GetBytes(stderr);
        var stderrStream = new MemoryStream(stderrBytes);
        var stderrReader = new StreamReader(stderrStream);

        var mock = new Mock<IProcessInstance>();
        mock.SetupGet(p => p.ExitCode).Returns(exitCode);
        mock.SetupGet(p => p.StandardOutput).Returns(stdoutReader);
        mock.SetupGet(p => p.StandardError).Returns(stderrReader);
        mock.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        return mock.Object;
    }

    [Fact]
    public async Task Start_ShouldSucceedAndResubscribeOnSuccessJson()
    {
        // Arrange
        string successJson = JsonSerializer.Serialize(new SubscriptionResult
        {
            Route = "subscription",
            NotificationReceived = true,
            FinalText = "New review event",
            RecommendedNextAction = "Review now",
            ServerUrl = "http://localhost:3000"
        });

        var preflightProcess = CreateMockProcess(0, "help", "");
        var subscriptionProcess = CreateMockProcess(0, successJson, "");

        var mockRunner = new Mock<IProcessRunner>();
        int startCount = 0;

        var testCts = new CancellationTokenSource();

        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Returns<ProcessStartInfo>(psi =>
            {
                startCount++;
                if (psi.ArgumentList.Contains("--help"))
                {
                    return preflightProcess;
                }

                testCts.Cancel();
                return subscriptionProcess;
            });

        Environment.SetEnvironmentVariable("MCP_PROBE_AUTH_TOKEN", "test-token-value");

        try
        {
            var service = new McpSubscriptionService(_settingsService, _notificationService, _loggingService, mockRunner.Object);

            var runMethod = typeof(McpSubscriptionService).GetMethod("RunSubscriptionLoopAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            runMethod.Should().NotBeNull();

            // Act
            var task = (Task)runMethod!.Invoke(service, new object[] { testCts.Token })!;
            await task;

            // Assert
            startCount.Should().Be(2); // 1 preflight + 1 subscription
            service.State.Should().Be(SubscriptionState.Running);
            service.LastError.Should().BeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCP_PROBE_AUTH_TOKEN", null);
        }
    }

    [Theory]
    [InlineData("failed", "ERR_01", "Gateway failed")]
    [InlineData("timeout", null, "Timeout occurred")]
    public async Task Start_ShouldTransitionToErrorOnFailureJson(string route, string? errorCode, string message)
    {
        // Arrange
        string failureJson = JsonSerializer.Serialize(new SubscriptionResult
        {
            Route = route,
            ErrorCode = errorCode,
            FinalText = message
        });

        var preflightProcess = CreateMockProcess(0, "help", "");
        var subscriptionProcess = CreateMockProcess(0, failureJson, "");

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Returns<ProcessStartInfo>(psi => psi.ArgumentList.Contains("--help") ? preflightProcess : subscriptionProcess);

        var service = new McpSubscriptionService(_settingsService, _notificationService, _loggingService, mockRunner.Object);
        var runMethod = typeof(McpSubscriptionService).GetMethod("RunSubscriptionLoopAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        var task = (Task)runMethod!.Invoke(service, new object[] { CancellationToken.None })!;
        await task;

        // Assert
        service.State.Should().Be(SubscriptionState.Error);
        service.LastError.Should().Contain(route);
    }

    [Theory]
    [InlineData("{invalid-json}")]
    [InlineData("")]
    [InlineData("{\"serverUrl\":\"http://foo\"}")]
    public async Task Start_ShouldTransitionToErrorOnInvalidOrMissingRouteJson(string badStdout)
    {
        // Arrange
        var preflightProcess = CreateMockProcess(0, "help", "");
        var subscriptionProcess = CreateMockProcess(0, badStdout, "");

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Returns<ProcessStartInfo>(psi => psi.ArgumentList.Contains("--help") ? preflightProcess : subscriptionProcess);

        var service = new McpSubscriptionService(_settingsService, _notificationService, _loggingService, mockRunner.Object);
        var runMethod = typeof(McpSubscriptionService).GetMethod("RunSubscriptionLoopAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        var task = (Task)runMethod!.Invoke(service, new object[] { CancellationToken.None })!;
        await task;

        // Assert
        service.State.Should().Be(SubscriptionState.Error);
        service.LastError.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Start_ShouldHoldNonZeroExitAndStderr()
    {
        // Arrange
        var preflightProcess = CreateMockProcess(0, "help", "");
        var subscriptionProcess = CreateMockProcess(1, "", "Execution failed dramatically");

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Returns<ProcessStartInfo>(psi => psi.ArgumentList.Contains("--help") ? preflightProcess : subscriptionProcess);

        var service = new McpSubscriptionService(_settingsService, _notificationService, _loggingService, mockRunner.Object);
        var runMethod = typeof(McpSubscriptionService).GetMethod("RunSubscriptionLoopAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        var task = (Task)runMethod!.Invoke(service, new object[] { CancellationToken.None })!;
        await task;

        // Assert
        service.State.Should().Be(SubscriptionState.Error);
        service.LastError.Should().Contain("non-zero code 1");
        service.LastError.Should().Contain("Execution failed dramatically");
    }

    [Fact]
    public async Task Start_ShouldPassArgumentsAndSecretsCorrectly()
    {
        // Arrange
        _settingsService.UpdateSettings("my-cmd", "--foo bar", "http://gateway:80", "queue://res", 30000);
        Environment.SetEnvironmentVariable("MCP_PROBE_AUTH_TOKEN", "super-secret-token");

        var preflightProcess = CreateMockProcess(0, "help", "");
        var subscriptionProcess = CreateMockProcess(0, JsonSerializer.Serialize(new SubscriptionResult { Route = "subscription" }), "");

        ProcessStartInfo? capturedPsi = null;

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Callback<ProcessStartInfo>(psi =>
            {
                if (!psi.ArgumentList.Contains("--help"))
                {
                    capturedPsi = psi;
                }
            })
            .Returns<ProcessStartInfo>(psi => psi.ArgumentList.Contains("--help") ? preflightProcess : subscriptionProcess);

        var service = new McpSubscriptionService(_settingsService, _notificationService, _loggingService, mockRunner.Object);
        var runMethod = typeof(McpSubscriptionService).GetMethod("RunSubscriptionLoopAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        var testCts = new CancellationTokenSource();
        testCts.CancelAfter(500);

        var task = Task.Run(async () =>
        {
            var t = (Task)runMethod!.Invoke(service, new object[] { testCts.Token })!;
            await t;
        });

        await Task.Delay(100);
        testCts.Cancel();
        try
        {
            await task;
        }
        catch
        {
            // ignore
        }

        // Assert
        capturedPsi.Should().NotBeNull();
        capturedPsi!.FileName.Should().Be("my-cmd");
        capturedPsi.ArgumentList.Should().ContainInOrder("--foo", "bar");
        capturedPsi.ArgumentList.Should().ContainInOrder("--url", "http://gateway:80");
        capturedPsi.ArgumentList.Should().ContainInOrder("--uri", "queue://res");
        capturedPsi.ArgumentList.Should().ContainInOrder("--timeout-ms", "30000");
        capturedPsi.ArgumentList.Should().Contain("--json");

        capturedPsi.Environment.Should().ContainKey("MCP_PROBE_AUTH_TOKEN");
        capturedPsi.Environment["MCP_PROBE_AUTH_TOKEN"].Should().Be("super-secret-token");

        string serialized = JsonSerializer.Serialize(_settingsService.Settings);
        serialized.Should().NotContain("super-secret-token");

        Environment.SetEnvironmentVariable("MCP_PROBE_AUTH_TOKEN", null);
    }

    [Fact]
    public async Task StopAsync_ShouldKillProcessTreeAndCancelLoop()
    {
        // Arrange
        var mockProcess = new Mock<IProcessInstance>();
        mockProcess.SetupGet(p => p.ExitCode).Returns(0);
        mockProcess.SetupGet(p => p.StandardOutput).Returns(new StreamReader(new MemoryStream()));
        mockProcess.SetupGet(p => p.StandardError).Returns(new StreamReader(new MemoryStream()));
        mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>())).Returns(Task.Delay(10000));

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(mockProcess.Object);

        var service = new McpSubscriptionService(_settingsService, _notificationService, _loggingService, mockRunner.Object);

        // Act
        var activeProcField = typeof(McpSubscriptionService).GetField("_activeProcess", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var activeCtsField = typeof(McpSubscriptionService).GetField("_activeProcessCts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        activeProcField.Should().NotBeNull();
        activeCtsField.Should().NotBeNull();

        activeProcField!.SetValue(service, mockProcess.Object);
        var dummyCts = new CancellationTokenSource();
        activeCtsField!.SetValue(service, dummyCts);

        await service.StopAsync();

        // Assert
        mockProcess.Verify(p => p.Kill(true), Times.Once);
        dummyCts.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task Start_ShouldHandleNotificationReceivedFalse()
    {
        // Arrange
        string resultJson = JsonSerializer.Serialize(new SubscriptionResult
        {
            Route = "subscription",
            NotificationReceived = false,
            FinalText = "No notification"
        });

        var preflightProcess = CreateMockProcess(0, "help", "");
        var subscriptionProcess = CreateMockProcess(0, resultJson, "");

        var mockRunner = new Mock<IProcessRunner>();
        var testCts = new CancellationTokenSource();

        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Returns<ProcessStartInfo>(psi =>
            {
                if (psi.ArgumentList.Contains("--help"))
                {
                    return preflightProcess;
                }

                testCts.Cancel();
                return subscriptionProcess;
            });

        var service = new McpSubscriptionService(_settingsService, _notificationService, _loggingService, mockRunner.Object);
        var runMethod = typeof(McpSubscriptionService).GetMethod("RunSubscriptionLoopAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        var task = (Task)runMethod!.Invoke(service, new object[] { testCts.Token })!;
        await task;

        // Assert
        service.State.Should().Be(SubscriptionState.Running);
    }

    [Fact]
    public async Task PreflightCheck_ShouldReturnFalseOnException()
    {
        // Arrange
        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Throws(new InvalidOperationException("Command not found"));

        var service = new McpSubscriptionService(_settingsService, _notificationService, _loggingService, mockRunner.Object);

        // Act
        bool result = await service.PreflightCheckAsync(CancellationToken.None);

        // Assert
        result.Should().BeFalse();
        service.LastError.Should().Contain("Command not found");
    }

    [Fact]
    public async Task Start_ShouldSkipTokenAndArgumentsWhenEmpty()
    {
        // Arrange
        _settingsService.UpdateSettings("my-cmd", "", "http://gateway:80", "queue://res", 30000);
        Environment.SetEnvironmentVariable("MCP_PROBE_AUTH_TOKEN", null);

        var preflightProcess = CreateMockProcess(0, "help", "");
        var subscriptionProcess = CreateMockProcess(0, JsonSerializer.Serialize(new SubscriptionResult { Route = "subscription" }), "");

        ProcessStartInfo? capturedPsi = null;
        var mockRunner = new Mock<IProcessRunner>();
        var testCts = new CancellationTokenSource();

        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Callback<ProcessStartInfo>(psi =>
            {
                if (!psi.ArgumentList.Contains("--help"))
                {
                    capturedPsi = psi;
                    testCts.Cancel();
                }
            })
            .Returns<ProcessStartInfo>(psi => psi.ArgumentList.Contains("--help") ? preflightProcess : subscriptionProcess);

        var service = new McpSubscriptionService(_settingsService, _notificationService, _loggingService, mockRunner.Object);
        var runMethod = typeof(McpSubscriptionService).GetMethod("RunSubscriptionLoopAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        var task = (Task)runMethod!.Invoke(service, new object[] { testCts.Token })!;
        await task;

        // Assert
        capturedPsi.Should().NotBeNull();
        capturedPsi!.Environment.Should().NotContainKey("MCP_PROBE_AUTH_TOKEN");
        capturedPsi.ArgumentList.Should().NotContain("--foo");

        Environment.SetEnvironmentVariable("MCP_PROBE_AUTH_TOKEN", null);
    }

    [Fact]
    public async Task PreflightCheck_ShouldReturnFalseWhenExitCodeIsNonZero()
    {
        // Arrange
        var mockProcess = CreateMockProcess(1, "", "");
        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(mockProcess);

        var service = new McpSubscriptionService(_settingsService, _notificationService, _loggingService, mockRunner.Object);

        // Act
        bool result = await service.PreflightCheckAsync(CancellationToken.None);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsync_ShouldKillActiveProcessAndCancelLoop()
    {
        // Arrange
        var mockProcess = new Mock<IProcessInstance>();
        mockProcess.SetupGet(p => p.ExitCode).Returns(0);
        mockProcess.SetupGet(p => p.StandardOutput).Returns(new StreamReader(new MemoryStream()));
        mockProcess.SetupGet(p => p.StandardError).Returns(new StreamReader(new MemoryStream()));
        mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>())).Returns(Task.Delay(10000));

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(mockProcess.Object);

        var service = new McpSubscriptionService(_settingsService, _notificationService, _loggingService, mockRunner.Object);

        var activeProcField = typeof(McpSubscriptionService).GetField("_activeProcess", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var activeCtsField = typeof(McpSubscriptionService).GetField("_activeProcessCts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        activeProcField.Should().NotBeNull();
        activeCtsField.Should().NotBeNull();

        activeProcField!.SetValue(service, mockProcess.Object);
        var dummyCts = new CancellationTokenSource();
        activeCtsField!.SetValue(service, dummyCts);

        // Act
        await service.DisposeAsync();

        // Assert
        mockProcess.Verify(p => p.Kill(true), Times.Once);
        dummyCts.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task Start_ShouldTransitionToErrorWhenOperationCanceledExceptionThrownWithoutLoopCancellation()
    {
        // Arrange
        var preflightProcess = CreateMockProcess(0, "help", "");

        var mockProcess = new Mock<IProcessInstance>();
        mockProcess.SetupGet(p => p.ExitCode).Returns(0);
        mockProcess.SetupGet(p => p.StandardOutput).Returns(new StreamReader(new MemoryStream()));
        mockProcess.SetupGet(p => p.StandardError).Returns(new StreamReader(new MemoryStream()));
        mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>()))
            .Throws(new OperationCanceledException("Process was canceled"));

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Returns<ProcessStartInfo>(psi => psi.ArgumentList.Contains("--help") ? preflightProcess : mockProcess.Object);

        var service = new McpSubscriptionService(_settingsService, _notificationService, _loggingService, mockRunner.Object);
        var runMethod = typeof(McpSubscriptionService).GetMethod("RunSubscriptionLoopAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        var task = (Task)runMethod!.Invoke(service, new object[] { CancellationToken.None })!;
        await task;

        // Assert
        service.State.Should().Be(SubscriptionState.Error);
        service.LastError.Should().Contain("was canceled");
    }

    [Fact]
    public void Start_ShouldReturnImmediatelyIfAlreadyRunningOrStarting()
    {
        // Arrange
        var mockRunner = new Mock<IProcessRunner>();
        var service = new McpSubscriptionService(_settingsService, _notificationService, _loggingService, mockRunner.Object);

        var stateProp = typeof(McpSubscriptionService).GetProperty("State", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        stateProp.Should().NotBeNull();
        stateProp!.SetValue(service, SubscriptionState.Running);

        // Act
        service.Start();

        // Assert
        mockRunner.Verify(r => r.Start(It.IsAny<ProcessStartInfo>()), Times.Never);
    }

    [Fact]
    public async Task StopAsync_ShouldDoNothingIfLoopTaskIsNull()
    {
        // Arrange
        var mockRunner = new Mock<IProcessRunner>();
        var service = new McpSubscriptionService(_settingsService, _notificationService, _loggingService, mockRunner.Object);

        // Act
        Func<Task> act = async () => await service.StopAsync();

        // Assert
        await act.Should().NotThrowAsync();
        service.State.Should().Be(SubscriptionState.Stopped);
    }
}
