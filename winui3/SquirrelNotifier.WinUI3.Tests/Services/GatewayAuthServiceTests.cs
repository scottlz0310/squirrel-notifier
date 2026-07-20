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

    private Mock<IProcessInstance> CreateMockProcess(int exitCode, string stdout, string stderr)
    {
        var mockProcess = new Mock<IProcessInstance>();
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
        var mockProcess = CreateMockProcess(0, stdout, string.Empty);

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(mockProcess.Object);

        var mockBrowser = new Mock<IBrowserLauncher>();
        mockBrowser.Setup(b => b.OpenUrl("https://mcp-gateway.example.com/device?user_code=ABCD-1234")).Returns(true);

        var service = new GatewayAuthService(_settingsService, _loggingService, _subscriptionService, mockRunner.Object, mockBrowser.Object);
        GatewayAuthProgress? reportedProgress = null;
        var progress = new Progress<GatewayAuthProgress>(p => reportedProgress = p);

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
        var mockProcess = CreateMockProcess(0, stdout, string.Empty);

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(mockProcess.Object);

        var mockBrowser = new Mock<IBrowserLauncher>();
        mockBrowser.Setup(b => b.OpenUrl(It.IsAny<string>())).Returns(false); // ブラウザ起動失敗

        var service = new GatewayAuthService(_settingsService, _loggingService, _subscriptionService, mockRunner.Object, mockBrowser.Object);

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
        var mockProcess = CreateMockProcess(1, string.Empty, stderr);

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(mockProcess.Object);

        var service = new GatewayAuthService(_settingsService, _loggingService, _subscriptionService, mockRunner.Object, null);

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
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var mockProcess = new Mock<IProcessInstance>();
        mockProcess.SetupGet(p => p.StandardOutput).Returns(new StreamReader(new MemoryStream(Array.Empty<byte>())));
        mockProcess.SetupGet(p => p.StandardError).Returns(new StreamReader(new MemoryStream(Array.Empty<byte>())));
        mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(mockProcess.Object);

        var service = new GatewayAuthService(_settingsService, _loggingService, _subscriptionService, mockRunner.Object, null);

        // Act
        GatewayAuthProgress result = await service.LoginAsync(null, cts.Token);

        // Assert
        result.Stage.Should().Be(GatewayAuthStage.Cancelled);
        mockProcess.Verify(p => p.Kill(true), Times.Once);
    }
}
