// <copyright file="TaskSchedulerServiceTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class TaskSchedulerServiceTests
{
    private static Mock<IProcessInstance> CreateMockProcess(int exitCode, string stderr = "")
    {
        var mock = new Mock<IProcessInstance>();
        mock.SetupGet(p => p.ExitCode).Returns(exitCode);
        mock.SetupGet(p => p.StandardError).Returns(new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(stderr))));
        mock.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return mock;
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    public async Task GetStatusAsync_ReturnsStatusBasedOnExitCode(int exitCode, bool expectRegistered)
    {
        // Arrange
        Mock<IProcessInstance> mockProcess = CreateMockProcess(exitCode);
        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(mockProcess.Object);

        var service = new TaskSchedulerService(mockRunner.Object);

        // Act
        TaskRegistrationStatus status = await service.GetStatusAsync();

        // Assert
        if (expectRegistered)
        {
            status.Should().Be(TaskRegistrationStatus.Registered);
        }
        else
        {
            status.Should().Be(TaskRegistrationStatus.NotRegistered);
        }
    }

    [Fact]
    public async Task GetStatusAsync_WhenStartThrows_ReturnsNotRegistered()
    {
        // Arrange
        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Throws(new System.ComponentModel.Win32Exception());

        var service = new TaskSchedulerService(mockRunner.Object);

        // Act
        TaskRegistrationStatus status = await service.GetStatusAsync();

        // Assert
        status.Should().Be(TaskRegistrationStatus.NotRegistered);
    }

    [Fact]
    public async Task RegisterAsync_WhenExitCodeZero_Succeeds()
    {
        // Arrange
        Mock<IProcessInstance> mockProcess = CreateMockProcess(0);
        ProcessStartInfo? capturedPsi = null;

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Callback<ProcessStartInfo>(psi => capturedPsi = psi)
            .Returns(mockProcess.Object);

        var service = new TaskSchedulerService(mockRunner.Object);

        // Act
        await service.RegisterAsync();

        // Assert
        capturedPsi.Should().NotBeNull();
        capturedPsi!.FileName.Should().Be("schtasks.exe");
        capturedPsi.Arguments.Should().Contain("/Create");
        capturedPsi.Arguments.Should().Contain("SquirrelNotifier");
        capturedPsi.Arguments.Should().Contain("--tray");
        capturedPsi.Arguments.Should().Contain("ONLOGON");
    }

    [Fact]
    public async Task RegisterAsync_WhenExitCodeNonZero_ThrowsInvalidOperationException()
    {
        // Arrange
        Mock<IProcessInstance> mockProcess = CreateMockProcess(1, "Access is denied.");
        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(mockProcess.Object);

        var service = new TaskSchedulerService(mockRunner.Object);

        // Act
        Func<Task> act = () => service.RegisterAsync();

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*タスクスケジューラへの登録に失敗しました*");
    }

    [Fact]
    public async Task UnregisterAsync_IgnoresExitCode()
    {
        // Arrange
        Mock<IProcessInstance> mockProcess = CreateMockProcess(1);
        ProcessStartInfo? capturedPsi = null;

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Callback<ProcessStartInfo>(psi => capturedPsi = psi)
            .Returns(mockProcess.Object);

        var service = new TaskSchedulerService(mockRunner.Object);

        // Act
        await service.UnregisterAsync();

        // Assert
        capturedPsi!.Arguments.Should().Contain("/Delete");
        capturedPsi.Arguments.Should().Contain("SquirrelNotifier");
    }

    [Fact]
    public async Task RepairAsync_CallsUnregisterThenRegister()
    {
        // Arrange
        var callOrder = new System.Collections.Generic.List<string>();

        Mock<IProcessInstance> deleteMock = CreateMockProcess(0);
        Mock<IProcessInstance> createMock = CreateMockProcess(0);

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.Is<ProcessStartInfo>(p => p.Arguments.Contains("/Delete"))))
            .Callback<ProcessStartInfo>(_ => callOrder.Add("Delete"))
            .Returns(deleteMock.Object);
        mockRunner.Setup(r => r.Start(It.Is<ProcessStartInfo>(p => p.Arguments.Contains("/Create"))))
            .Callback<ProcessStartInfo>(_ => callOrder.Add("Create"))
            .Returns(createMock.Object);

        var service = new TaskSchedulerService(mockRunner.Object);

        // Act
        await service.RepairAsync();

        // Assert
        callOrder.Should().Equal("Delete", "Create");
    }
}
