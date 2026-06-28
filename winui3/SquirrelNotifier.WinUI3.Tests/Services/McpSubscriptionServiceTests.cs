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
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class McpSubscriptionServiceTests : IDisposable
{
    private readonly string _settingsDirectory;
    private readonly SettingsService _settingsService;
    private readonly Mock<INotificationService> _mockNotificationService;
    private readonly INotificationService _notificationService;
    private readonly LoggingService _loggingService;

    public McpSubscriptionServiceTests()
    {
        _settingsDirectory = Path.Combine(Path.GetTempPath(), $"McpSubscriptionServiceTests_{Guid.NewGuid()}");
        _settingsService = new SettingsService(_settingsDirectory);
        _mockNotificationService = new Mock<INotificationService>();
        _notificationService = _mockNotificationService.Object;
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

        var service = new McpSubscriptionService(_settingsService, _notificationService, _loggingService, mockRunner.Object, maxRetries: 0);
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

        var service = new McpSubscriptionService(_settingsService, _notificationService, _loggingService, mockRunner.Object, maxRetries: 0);
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

        var service = new McpSubscriptionService(_settingsService, _notificationService, _loggingService, mockRunner.Object, maxRetries: 0);
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
        _settingsService.UpdateSettings("my-cmd", "--foo bar", "http://gateway:80", new[] { "queue://res" }, 30000, "review-raven", "", "review-raven", "", "reviewer", 300000);
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
        _settingsService.UpdateSettings("my-cmd", "", "http://gateway:80", new[] { "queue://res" }, 30000, "review-raven", "", "review-raven", "", "reviewer", 300000);
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

        var service = new McpSubscriptionService(_settingsService, _notificationService, _loggingService, mockRunner.Object, maxRetries: 0);
        var runMethod = typeof(McpSubscriptionService).GetMethod("RunSubscriptionLoopAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        var task = (Task)runMethod!.Invoke(service, new object[] { CancellationToken.None })!;
        await task;

        // Assert: OCE without loop/process cancellation is treated as an error and triggers retry/error path
        service.State.Should().Be(SubscriptionState.Error);
        service.LastError.Should().Contain("canceled");
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

    [Fact]
    public async Task PreflightCheck_ShouldKillProcessOnCancellation()
    {
        // Arrange
        var mockProcess = new Mock<IProcessInstance>();
        mockProcess.SetupGet(p => p.ExitCode).Returns(0);
        mockProcess.SetupGet(p => p.StandardOutput).Returns(new StreamReader(new MemoryStream()));
        mockProcess.SetupGet(p => p.StandardError).Returns(new StreamReader(new MemoryStream()));
        mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(async ct => await Task.Delay(10000, ct));

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(mockProcess.Object);

        var service = new McpSubscriptionService(_settingsService, _notificationService, _loggingService, mockRunner.Object);

        var cts = new CancellationTokenSource();
        var task = service.PreflightCheckAsync(cts.Token);

        await Task.Delay(100);
        await service.StopAsync();

        // Assert
        mockProcess.Verify(p => p.Kill(true), Times.Once);
        var result = await task;
        result.Should().BeFalse();
    }

    [Fact]
    public async Task IntegrationTest_ShouldExecuteCommandSuccessfully()
    {
        // Arrange
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"IntegrationTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        var testCmd = Path.Combine(tempDir, "test-helper.cmd");
        await File.WriteAllTextAsync(testCmd, "@echo off\r\nif \"%1\"==\"--help\" (\r\n    exit /b 0\r\n)\r\nexit /b 1\r\n", Encoding.ASCII);

        var settingsService = new SettingsService(tempDir);
        settingsService.UpdateSettings(testCmd, "", "http://localhost:3000", new[] { "queue://res" }, 30000, "review-raven", "", "review-raven", "", "reviewer", 300000);

        var runner = new ProcessRunner();
        await using var service = new McpSubscriptionService(settingsService, _notificationService, _loggingService, runner);

        try
        {
            // Act
            var result = await service.PreflightCheckAsync(CancellationToken.None);

            // Assert
            result.Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    [Fact]
    public async Task IntegrationTest_ShouldResolveAndRunSubscriberFromPathWithSpaces()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var baseTempDir = Path.Combine(Path.GetTempPath(), $"IntegrationTests_{Guid.NewGuid()}");
        var testDir = Path.Combine(baseTempDir, "Test Folder With Spaces");
        Directory.CreateDirectory(testDir);

        var cmdPath = Path.Combine(testDir, "mcp-resource-subscriber.cmd");
        var resultJson = JsonSerializer.Serialize(new SubscriptionResult
        {
            Route = "subscription",
            NotificationReceived = true,
            FinalText = "Notification from dummy CLI",
            RecommendedNextAction = "Check reviews"
        });

        var scriptContent = $"@echo off\r\nif \"%1\"==\"--help\" (\r\n    exit /b 0\r\n)\r\necho {resultJson}\r\nping 127.0.0.1 -n 10 > nul\r\nexit /b 0\r\n";
        await File.WriteAllTextAsync(cmdPath, scriptContent, Encoding.ASCII);

        var shimPath = Path.Combine(testDir, "mcp-resource-subscriber");
        await File.WriteAllTextAsync(shimPath, "@echo off\r\necho ERROR: Extensionless shim executed!\r\nexit /b 1\r\n", Encoding.ASCII);

        var oldPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", $"{testDir};{oldPath}");

        var settingsDir = Path.Combine(baseTempDir, "settings");
        Directory.CreateDirectory(settingsDir);

        try
        {
            var settingsService = new SettingsService(settingsDir);
            settingsService.Settings.SubscriberCommandPath.Should().Be(Path.GetFullPath(cmdPath));

            var runner = new ProcessRunner();
            await using var service = new McpSubscriptionService(settingsService, _notificationService, _loggingService, runner);

            var preflightResult = await service.PreflightCheckAsync(CancellationToken.None);
            preflightResult.Should().BeTrue();

            var cts = new CancellationTokenSource();
            service.Start();

            await Task.Delay(2000);

            service.State.Should().Be(SubscriptionState.Running, because: service.LastError);
            service.LastError.Should().BeEmpty();

            await service.StopAsync();
            service.State.Should().Be(SubscriptionState.Stopped);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            if (Directory.Exists(baseTempDir))
            {
                try
                {
                    Directory.Delete(baseTempDir, true);
                }
                catch
                {
                    // ignore
                }
            }
        }
    }



    [Theory]
    [InlineData("arg1 arg2", new[] { "arg1", "arg2" })]
    [InlineData("\"C:\\\\Program Files\\\\app.js\" --arg", new[] { "C:\\\\Program Files\\\\app.js", "--arg" })]
    [InlineData("--profile \"my queue\"", new[] { "--profile", "my queue" })]
    [InlineData("", new string[0])]
    [InlineData("\"\" --flag", new[] { "", "--flag" })]
    [InlineData("arg1\targ2", new[] { "arg1", "arg2" })]
    [InlineData("--label \"review \\\"queue\\\"\"", new[] { "--label", "review \"queue\"" })]
    public void ParseArguments_ShouldParseComplexArgumentsCorrectly(string input, string[] expected)
    {
        // Act
        var result = McpSubscriptionService.ParseArguments(input);

        // Assert
        result.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void Deduplication_ShouldWorkCorrectly()
    {
        // Arrange
        var mockRunner = new Mock<IProcessRunner>();
        var service = new McpSubscriptionService(_settingsService, _notificationService, _loggingService, mockRunner.Object);
        var hasBeenSeenMethod = typeof(McpSubscriptionService).GetMethod("HasBeenSeen", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var markAsSeenMethod = typeof(McpSubscriptionService).GetMethod("MarkAsSeen", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        hasBeenSeenMethod.Should().NotBeNull();
        markAsSeenMethod.Should().NotBeNull();

        // Act & Assert
        // First check: not seen
        var result1 = (bool)hasBeenSeenMethod!.Invoke(service, new object[] { "evt_1" })!;
        result1.Should().BeFalse();

        // Mark as seen
        markAsSeenMethod!.Invoke(service, new object[] { "evt_1", null! });

        // Second check: seen
        var result2 = (bool)hasBeenSeenMethod.Invoke(service, new object[] { "evt_1" })!;
        result2.Should().BeTrue();

        // Different ID: not seen
        var result3 = (bool)hasBeenSeenMethod.Invoke(service, new object[] { "evt_2" })!;
        result3.Should().BeFalse();
    }

    [Fact]
    public async Task Start_ShouldRetryOnTransientErrorAndRecoverOnSuccess()
    {
        // Arrange
        string successJson = JsonSerializer.Serialize(new SubscriptionResult
        {
            Route = "subscription",
            NotificationReceived = true,
            FinalText = "Notification received",
        });

        var preflightProcess = CreateMockProcess(0, "help", "");
        var failureProcess = CreateMockProcess(1, "", "transient error");
        var successProcess = CreateMockProcess(0, successJson, "");

        var mockRunner = new Mock<IProcessRunner>();
        int subscriptionCallCount = 0;
        var testCts = new CancellationTokenSource();

        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Returns<ProcessStartInfo>(psi =>
            {
                if (psi.ArgumentList.Contains("--help"))
                {
                    return preflightProcess;
                }

                subscriptionCallCount++;
                if (subscriptionCallCount == 1)
                {
                    return failureProcess;
                }

                testCts.Cancel();
                return successProcess;
            });

        var service = new McpSubscriptionService(_settingsService, _notificationService, _loggingService, mockRunner.Object, maxRetries: 1);
        var runMethod = typeof(McpSubscriptionService).GetMethod("RunSubscriptionLoopAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        var task = (Task)runMethod!.Invoke(service, new object[] { testCts.Token })!;
        await task;

        // Assert
        subscriptionCallCount.Should().Be(2);
        service.State.Should().Be(SubscriptionState.Running);
        service.LastError.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_ShouldTransitionToErrorAfterMaxRetries()
    {
        // Arrange
        var preflightProcess = CreateMockProcess(0, "help", "");

        var mockRunner = new Mock<IProcessRunner>();
        int subscriptionCallCount = 0;
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Returns<ProcessStartInfo>(psi =>
            {
                if (psi.ArgumentList.Contains("--help"))
                {
                    return preflightProcess;
                }

                subscriptionCallCount++;
                return CreateMockProcess(1, "", "persistent error");
            });

        // maxRetries: 1 → 1 initial + 1 retry = 2 total subscription calls
        var service = new McpSubscriptionService(_settingsService, _notificationService, _loggingService, mockRunner.Object, maxRetries: 1);
        var runMethod = typeof(McpSubscriptionService).GetMethod("RunSubscriptionLoopAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        var task = (Task)runMethod!.Invoke(service, new object[] { CancellationToken.None })!;
        await task;

        // Assert
        subscriptionCallCount.Should().Be(2);
        service.State.Should().Be(SubscriptionState.Error);
        service.LastError.Should().Contain("non-zero code 1");
        service.LastError.Should().Contain("persistent error");
    }

    [Fact]
    public async Task StopAsync_ShouldCancelBackoffDelayPromptly()
    {
        // Arrange
        var preflightProcess = CreateMockProcess(0, "help", "");
        var mockRunner = new Mock<IProcessRunner>();
        var retryingTcs = new TaskCompletionSource();

        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Returns<ProcessStartInfo>(psi =>
            {
                if (psi.ArgumentList.Contains("--help"))
                {
                    return preflightProcess;
                }

                return CreateMockProcess(1, "", "transient error");
            });

        var service = new McpSubscriptionService(_settingsService, _notificationService, _loggingService, mockRunner.Object, maxRetries: 5);
        service.StatusTextChanged += (_, text) =>
        {
            if (text.StartsWith("Retrying"))
            {
                retryingTcs.TrySetResult();
            }
        };

        service.Start();

        // Wait until backoff starts (first failure → "Retrying" status)
        await retryingTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Act: StopAsync should cancel the backoff delay (1000ms) and return promptly
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await service.StopAsync();
        sw.Stop();

        // Assert: completed well under the 1000ms backoff delay
        sw.ElapsedMilliseconds.Should().BeLessThan(800);
        service.State.Should().Be(SubscriptionState.Stopped);
    }

    [Fact]
    public void Deduplication_ShouldLimitCacheSizeTo100()
    {
        // Arrange
        var mockRunner = new Mock<IProcessRunner>();
        var service = new McpSubscriptionService(_settingsService, _notificationService, _loggingService, mockRunner.Object);
        var hasBeenSeenMethod = typeof(McpSubscriptionService).GetMethod("HasBeenSeen", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var markAsSeenMethod = typeof(McpSubscriptionService).GetMethod("MarkAsSeen", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        hasBeenSeenMethod.Should().NotBeNull();
        markAsSeenMethod.Should().NotBeNull();

        // Add 101 items
        for (int i = 0; i < 101; i++)
        {
            markAsSeenMethod!.Invoke(service, new object[] { $"evt_{i}", null! });
        }

        // The first item should be evicted now, so it should not be detected as seen
        var result = (bool)hasBeenSeenMethod!.Invoke(service, new object[] { "evt_0" })!;
        result.Should().BeFalse();
    }

    [Fact]
    public async Task CacheService_ShouldRestoreSeenIdsOnStart()
    {
        // Arrange: キャッシュに evt_cached を保存
        var mockCacheService = new Mock<ICacheService>();
        mockCacheService.Setup(c => c.LoadAsync()).ReturnsAsync(new NotificationCache
        {
            SeenEventIds = new List<string> { "evt_cached" },
            RecentEvents = new List<CachedReviewEvent>(),
        });
        mockCacheService.Setup(c => c.SaveAsync(It.IsAny<NotificationCache>())).Returns(Task.CompletedTask);

        string eventJson = JsonSerializer.Serialize(new SubscriptionResult
        {
            Route = "subscription",
            NotificationReceived = true,
            FinalText = @"{""eventId"":""evt_cached"",""repository"":""owner/repo"",""prNumber"":1,""prUrl"":""https://github.com/owner/repo/pull/1"",""reason"":""review_requested"",""source"":""thread-owl"",""message"":""Review requested""}",
        });

        var preflightProcess = CreateMockProcess(0, "help", "");
        var subscriptionProcess = CreateMockProcess(0, eventJson, "");

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

        var service = new McpSubscriptionService(_settingsService, _notificationService, _loggingService, mockRunner.Object, cacheService: mockCacheService.Object);
        var runMethod = typeof(McpSubscriptionService).GetMethod("RunSubscriptionLoopAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        var task = (Task)runMethod!.Invoke(service, new object[] { testCts.Token })!;
        await task;

        // Assert: 既にキャッシュにあった evt_cached は通知されない
        _mockNotificationService.Verify(n => n.NotifyReviewEvent(It.IsAny<ReviewEvent>()), Times.Never);
    }

    [Fact]
    public async Task CacheService_ShouldSaveAfterNewNotification()
    {
        // Arrange
        var mockCacheService = new Mock<ICacheService>();
        mockCacheService.Setup(c => c.LoadAsync()).ReturnsAsync(new NotificationCache());
        mockCacheService.Setup(c => c.SaveAsync(It.IsAny<NotificationCache>())).Returns(Task.CompletedTask);

        string eventJson = JsonSerializer.Serialize(new SubscriptionResult
        {
            Route = "subscription",
            NotificationReceived = true,
            FinalText = @"{""eventId"":""evt_new"",""repository"":""owner/repo"",""prNumber"":2,""prUrl"":""https://github.com/owner/repo/pull/2"",""reason"":""review_requested"",""source"":""thread-owl"",""message"":""Review requested""}",
        });

        var preflightProcess = CreateMockProcess(0, "help", "");
        var subscriptionProcess = CreateMockProcess(0, eventJson, "");

        var mockRunner = new Mock<IProcessRunner>();
        var testCts = new CancellationTokenSource();

        // 2回目のStart呼び出し（再購読）でキャンセルする
        // 1回目のStart呼び出し時点でキャンセルすると processToken が即キャンセルされ
        // ReadToEndAsync がOCEを投げてPersistCacheAsyncに到達しないため
        int subscriptionCallCount = 0;
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Returns<ProcessStartInfo>(psi =>
            {
                if (psi.ArgumentList.Contains("--help"))
                {
                    return preflightProcess;
                }

                subscriptionCallCount++;
                if (subscriptionCallCount >= 2)
                {
                    testCts.Cancel();
                }

                return subscriptionProcess;
            });

        var service = new McpSubscriptionService(_settingsService, _notificationService, _loggingService, mockRunner.Object, cacheService: mockCacheService.Object);
        var runMethod = typeof(McpSubscriptionService).GetMethod("RunSubscriptionLoopAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        var task = (Task)runMethod!.Invoke(service, new object[] { testCts.Token })!;
        await task;

        // Assert: 新規通知後にキャッシュが保存される
        mockCacheService.Verify(c => c.SaveAsync(It.Is<NotificationCache>(nc => nc.SeenEventIds.Contains("evt_new"))), Times.Once);
    }

    [Fact]
    public async Task Start_WithMultipleResourceUris_ShouldLaunchProcessForEachUri()
    {
        // Arrange: 2つの URI を設定
        _settingsService.UpdateSettings(
            "my-cmd", "", "http://gateway:80",
            new[] { "queue://uri-one", "queue://uri-two" },
            30000, "review-raven", "", "review-raven", "", "reviewer", 300000);

        var preflightProcess = CreateMockProcess(0, "help", "");
        var launchedUris = new System.Collections.Concurrent.ConcurrentBag<string>();
        var bothStartedTcs = new TaskCompletionSource();

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Returns<ProcessStartInfo>(psi =>
            {
                if (psi.ArgumentList.Contains("--help"))
                {
                    return preflightProcess;
                }

                int uriIndex = new List<string>(psi.ArgumentList).IndexOf("--uri");
                if (uriIndex >= 0 && uriIndex + 1 < psi.ArgumentList.Count)
                {
                    launchedUris.Add(psi.ArgumentList[uriIndex + 1]);
                    if (launchedUris.Count >= 2)
                    {
                        bothStartedTcs.TrySetResult();
                    }
                }

                var mock = new Mock<IProcessInstance>();
                mock.SetupGet(p => p.ExitCode).Returns(0);
                mock.SetupGet(p => p.StandardOutput).Returns(new StreamReader(new MemoryStream(Array.Empty<byte>())));
                mock.SetupGet(p => p.StandardError).Returns(new StreamReader(new MemoryStream(Array.Empty<byte>())));
                mock.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>()))
                    .Returns<CancellationToken>(ct => Task.Delay(Timeout.Infinite, ct));
                return mock.Object;
            });

        var service = new McpSubscriptionService(_settingsService, _notificationService, _loggingService, mockRunner.Object);

        // Act
        service.Start();
        await bothStartedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync();

        // Assert: 両方の URI に対してプロセスが起動された
        launchedUris.Should().Contain("queue://uri-one");
        launchedUris.Should().Contain("queue://uri-two");
    }

    [Fact]
    public async Task StopAsync_WithMultipleResourceUris_ShouldStopAllLoopsPromptly()
    {
        // Arrange: 2つの URI を設定し、両ループがバックオフ待機中に StopAsync を呼ぶ
        _settingsService.UpdateSettings(
            "my-cmd", "", "http://gateway:80",
            new[] { "queue://uri-one", "queue://uri-two" },
            30000, "review-raven", "", "review-raven", "", "reviewer", 300000);

        var preflightProcess = CreateMockProcess(0, "help", "");
        int retryingCount = 0;
        var bothRetryingTcs = new TaskCompletionSource();

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Returns<ProcessStartInfo>(psi =>
            {
                if (psi.ArgumentList.Contains("--help"))
                {
                    return preflightProcess;
                }

                return CreateMockProcess(1, "", "transient error");
            });

        var service = new McpSubscriptionService(_settingsService, _notificationService, _loggingService, mockRunner.Object, maxRetries: 5);
        service.StatusTextChanged += (_, text) =>
        {
            if (text.StartsWith("Retrying"))
            {
                int count = Interlocked.Increment(ref retryingCount);
                if (count >= 2)
                {
                    bothRetryingTcs.TrySetResult();
                }
            }
        };

        service.Start();
        await bothRetryingTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Act: 両ループがバックオフ待機中に StopAsync を呼ぶ
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await service.StopAsync();
        sw.Stop();

        // Assert: 1000ms のバックオフ遅延より十分早く完了する
        sw.ElapsedMilliseconds.Should().BeLessThan(800);
        service.State.Should().Be(SubscriptionState.Stopped);
    }

    [Theory]
    [InlineData("fetch failed", "mcp-gateway への接続に失敗しました。mcp-gateway コンテナが起動しているか、または Gateway URL の設定が正しいか確認してください。", "[CONN_REFUSED]")]
    [InlineData("connect ECONNREFUSED 127.0.0.1:8080", "mcp-gateway への接続に失敗しました。mcp-gateway コンテナが起動しているか、または Gateway URL の設定が正しいか確認してください。", "[CONN_REFUSED]")]
    [InlineData("404 page not found", "指定されたエンドポイントが見つかりませんでした (404)。Gateway URL のポート番号やパスプレフィックス、または Resource URI が正しいか確認してください。", "[HTTP_404]")]
    [InlineData("Error POSTing to endpoint: 404", "指定されたエンドポイントが見つかりませんでした (404)。Gateway URL のポート番号やパスプレフィックス、または Resource URI が正しいか確認してください。", "[HTTP_404]")]
    [InlineData("Unauthorized access", "mcp-gateway で認証エラーが発生しました。認証トークン（MCP_PROBE_AUTH_TOKEN）の設定を確認してください。", "[AUTH_ERROR]")]
    [InlineData("401 Unauthorized", "mcp-gateway で認証エラーが発生しました。認証トークン（MCP_PROBE_AUTH_TOKEN）の設定を確認してください。", "[AUTH_ERROR]")]
    [InlineData("something went wrong", "予期しないエラーが発生しました: something went wrong", "[GENERAL_ERROR]")]
    [InlineData("", "不明なエラーが発生しました。", "[UNKNOWN_ERROR]")]
    [InlineData(null, "不明なエラーが発生しました。", "[UNKNOWN_ERROR]")]
    public void ErrorMessageMapping_ShouldReturnFriendlyMessageAndTag(string? input, string expectedFriendly, string expectedTag)
    {
        // Act
        var (friendlyResult, tagResult) = McpSubscriptionService.GetErrorInfo(input!);

        // Assert
        friendlyResult.Should().Be(expectedFriendly);
        tagResult.Should().Be(expectedTag);
    }
}
