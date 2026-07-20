// <copyright file="GatewayAuthServiceTests.cs" company="PlaceholderCompany">
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

public class GatewayAuthServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SettingsService _settingsService;
    private readonly LoggingService _loggingService;
    private readonly McpSubscriptionService _subscriptionService;

    public GatewayAuthServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"GatewayAuthServiceTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _settingsService = new SettingsService(_tempDir);
        _loggingService = new LoggingService(_tempDir);
        _subscriptionService = new McpSubscriptionService(_settingsService, _loggingService);
    }

    public void Dispose()
    {
        _subscriptionService.Dispose();
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch
            {
            }
        }
    }

    private static Mock<IProcessInstance> CreateMockProcess(int exitCode, string stdout, string stderr)
    {
        Mock<IProcessInstance> mockProcess = new();
        mockProcess.SetupGet(p => p.ExitCode).Returns(exitCode);
        mockProcess.SetupGet(p => p.StandardOutput).Returns(new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(stdout))));
        mockProcess.SetupGet(p => p.StandardError).Returns(new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(stderr))));
        mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return mockProcess;
    }

    [Fact]
    public async Task LoginAsync_ShouldExtractUrlAndUserCode_AndReturnSuccess_WhenExitCodeIsZero()
    {
        // Arrange
        string stdout = "Please visit https://mcp-gateway.example.com/device?user_code=ABCD-1234 to authenticate.\nuser_code: ABCD-1234\n";
        Mock<IProcessInstance> mockProcess = CreateMockProcess(0, stdout, string.Empty);

        Mock<IProcessRunner> mockRunner = new();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(mockProcess.Object);

        Mock<IBrowserLauncher> mockBrowser = new();
        mockBrowser.Setup(b => b.OpenUrl("https://mcp-gateway.example.com/device?user_code=ABCD-1234")).Returns(true);

        GatewayAuthService service = new(_settingsService, _loggingService, _subscriptionService, mockRunner.Object, mockBrowser.Object);
        GatewayAuthProgress? reportedProgress = null;
        Progress<GatewayAuthProgress> progress = new(p => reportedProgress = p);

        // Act
        GatewayAuthProgress result = await service.LoginAsync(progress, CancellationToken.None);

        // Assert
        result.Stage.Should().Be(GatewayAuthStage.Success);
        result.VerificationUrl.Should().Be("https://mcp-gateway.example.com/device?user_code=ABCD-1234");
        result.UserCode.Should().Be("ABCD-1234");
        result.BrowserLaunchFailed.Should().BeFalse();

        mockBrowser.Verify(b => b.OpenUrl("https://mcp-gateway.example.com/device?user_code=ABCD-1234"), Times.Once);
    }

    [Fact]
    public async Task LoginAsync_ShouldContinueAuthentication_WhenBrowserLaunchFails()
    {
        // Arrange
        string stdout = "Open URL: https://mcp-gateway.example.com/device\nUser Code: XYZ-9999\n";
        Mock<IProcessInstance> mockProcess = CreateMockProcess(0, stdout, string.Empty);

        Mock<IProcessRunner> mockRunner = new();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(mockProcess.Object);

        Mock<IBrowserLauncher> mockBrowser = new();
        mockBrowser.Setup(b => b.OpenUrl(It.IsAny<string>())).Returns(false); // ブラウザ起動失敗

        GatewayAuthService service = new(_settingsService, _loggingService, _subscriptionService, mockRunner.Object, mockBrowser.Object);

        // Act
        GatewayAuthProgress result = await service.LoginAsync(null, CancellationToken.None);

        // Assert
        result.Stage.Should().Be(GatewayAuthStage.Success);
        result.VerificationUrl.Should().Be("https://mcp-gateway.example.com/device");
        result.UserCode.Should().Be("XYZ-9999");
        result.BrowserLaunchFailed.Should().BeTrue();
    }

    [Fact]
    public async Task LoginAsync_ShouldReturnFailed_WhenProcessFailsWithNonZeroExitCode()
    {
        // Arrange
        string stderr = "Error: invalid client credentials";
        Mock<IProcessInstance> mockProcess = CreateMockProcess(1, string.Empty, stderr);

        Mock<IProcessRunner> mockRunner = new();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(mockProcess.Object);

        GatewayAuthService service = new(_settingsService, _loggingService, _subscriptionService, mockRunner.Object, null);

        // Act
        GatewayAuthProgress result = await service.LoginAsync(null, CancellationToken.None);

        // Assert
        result.Stage.Should().Be(GatewayAuthStage.Failed);
        result.ErrorMessage.Should().Contain("invalid client credentials");
    }

    [Fact]
    public async Task LoginAsync_ShouldReturnCancelled_WhenCancellationTokenIsCancelled()
    {
        // Arrange
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Mock<IProcessInstance> mockProcess = new();
        mockProcess.SetupGet(p => p.StandardOutput).Returns(new StreamReader(new MemoryStream(Array.Empty<byte>())));
        mockProcess.SetupGet(p => p.StandardError).Returns(new StreamReader(new MemoryStream(Array.Empty<byte>())));
        mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());

        Mock<IProcessRunner> mockRunner = new();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(mockProcess.Object);

        GatewayAuthService service = new(_settingsService, _loggingService, _subscriptionService, mockRunner.Object, null);

        // Act
        GatewayAuthProgress result = await service.LoginAsync(null, cts.Token);

        // Assert
        result.Stage.Should().Be(GatewayAuthStage.Cancelled);
        mockProcess.Verify(p => p.Kill(true), Times.Once);
    }

    [Theory]
    [InlineData("Error with Bearer secret-token-1234 and token=5678", "Error with Bearer *** and token=***")]
    [InlineData("token=abc+def/ghi== and access_token=xyz%20123", "token=*** and access_token=***")]
    [InlineData("", "")]
    public void SanitizeLogMessage_ShouldMaskTokensAndBearerHeaders(string raw, string expected)
    {
        // Act
        string sanitized = GatewayAuthService.SanitizeLogMessage(raw);

        // Assert
        sanitized.Should().Be(expected);
    }

    [Fact]
    public async Task LoginAsync_ShouldReturnTimeout_WhenProcessTimesOutWithoutUserCancellation()
    {
        // Arrange
        using CancellationTokenSource cts = new();
        Mock<IProcessInstance> mockProcess = new();
        mockProcess.SetupGet(p => p.StandardOutput).Returns(new StreamReader(new MemoryStream(Array.Empty<byte>())));
        mockProcess.SetupGet(p => p.StandardError).Returns(new StreamReader(new MemoryStream(Array.Empty<byte>())));
        mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());

        Mock<IProcessRunner> mockRunner = new();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(mockProcess.Object);

        GatewayAuthService service = new(_settingsService, _loggingService, _subscriptionService, mockRunner.Object, null);

        // Act
        GatewayAuthProgress result = await service.LoginAsync(null, cts.Token);

        // Assert
        result.Stage.Should().Be(GatewayAuthStage.Timeout);
        mockProcess.Verify(p => p.Kill(true), Times.Once);
    }

    [Fact]
    public async Task LoginAsync_ShouldReturnFailed_WhenProcessRunnerThrowsException()
    {
        // Arrange
        Mock<IProcessRunner> mockRunner = new();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Throws(new InvalidOperationException("Executable not found"));

        GatewayAuthService service = new(_settingsService, _loggingService, _subscriptionService, mockRunner.Object, null);

        // Act
        GatewayAuthProgress result = await service.LoginAsync(null, CancellationToken.None);

        // Assert
        result.Stage.Should().Be(GatewayAuthStage.Failed);
        result.ErrorMessage.Should().Contain("Executable not found");
    }

    [Fact]
    public async Task LoginAsync_ShouldRestartSubscription_WhenLoginSucceedsAndServiceIsStopped()
    {
        // Arrange
        Mock<IProcessInstance> mockProcess = CreateMockProcess(0, string.Empty, string.Empty);
        Mock<IProcessRunner> mockRunner = new();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(mockProcess.Object);

        GatewayAuthService service = new(_settingsService, _loggingService, _subscriptionService, mockRunner.Object, null);

        // Act
        GatewayAuthProgress result = await service.LoginAsync(null, CancellationToken.None);

        // Assert
        result.Stage.Should().Be(GatewayAuthStage.Success);
        _subscriptionService.State.Should().Be(SubscriptionState.Running);
    }

    [Fact]
    public async Task LoginAsync_ShouldPassSubscriberArgumentsAndGatewayUrl_ToProcessStartInfo()
    {
        // Arrange
        _settingsService.UpdateSettings(new AppSettings
        {
            SubscriberArguments = "--skip-check --verbose",
            GatewayUrl = "http://localhost:9999/mcp",
        });

        ProcessStartInfo? capturedPsi = null;
        Mock<IProcessInstance> mockProcess = CreateMockProcess(0, string.Empty, string.Empty);
        Mock<IProcessRunner> mockRunner = new();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Callback<ProcessStartInfo>(psi => capturedPsi = psi)
            .Returns(mockProcess.Object);

        GatewayAuthService service = new(_settingsService, _loggingService, _subscriptionService, mockRunner.Object, null);

        // Act
        await service.LoginAsync(null, CancellationToken.None);

        // Assert
        capturedPsi.Should().NotBeNull();
        capturedPsi!.ArgumentList.Should().Contain("--login");
        capturedPsi.ArgumentList.Should().Contain("--skip-check");
        capturedPsi.ArgumentList.Should().Contain("--verbose");
        capturedPsi.ArgumentList.Should().Contain("--url");
        capturedPsi.ArgumentList.Should().Contain("http://localhost:9999/mcp");
    }
}
