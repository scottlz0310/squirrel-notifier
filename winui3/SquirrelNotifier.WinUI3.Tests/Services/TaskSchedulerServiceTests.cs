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
        capturedPsi!.FileName.Should().Be("powershell.exe");
        string command = capturedPsi.ArgumentList[3];
        command.Should().Contain("Register-ScheduledTask");
        command.Should().Contain("Squirrel Notifier");
        command.Should().Contain("--tray");
        command.Should().Contain("AtLogOn");
        command.Should().Contain("Limited");
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
    public async Task UnregisterAsync_WhenExitCodeZero_Succeeds()
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
        await service.UnregisterAsync();

        // Assert
        capturedPsi!.FileName.Should().Be("powershell.exe");
        string command = capturedPsi.ArgumentList[3];
        command.Should().Contain("Unregister-ScheduledTask");
        command.Should().Contain("Squirrel Notifier");
    }

    [Fact]
    public async Task UnregisterAsync_WhenExitCodeNonZero_ThrowsInvalidOperationException()
    {
        // Arrange
        Mock<IProcessInstance> mockProcess = CreateMockProcess(1, "Access is denied.");
        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(mockProcess.Object);

        var service = new TaskSchedulerService(mockRunner.Object);

        // Act
        Func<Task> act = () => service.UnregisterAsync();

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*タスクスケジューラからの削除に失敗しました*");
    }

    [Fact]
    public async Task RepairAsync_WhenRegistered_CallsGetStatusThenDeleteThenCreate()
    {
        // Arrange
        var callOrder = new System.Collections.Generic.List<string>();

        Mock<IProcessInstance> queryMock = CreateMockProcess(0);
        Mock<IProcessInstance> deleteMock = CreateMockProcess(0);
        Mock<IProcessInstance> createMock = CreateMockProcess(0);

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.Is<ProcessStartInfo>(p => PsCommand(p).Contains("Get-ScheduledTask"))))
            .Callback<ProcessStartInfo>(_ => callOrder.Add("Query"))
            .Returns(queryMock.Object);
        mockRunner.Setup(r => r.Start(It.Is<ProcessStartInfo>(p => PsCommand(p).Contains("Unregister-ScheduledTask"))))
            .Callback<ProcessStartInfo>(_ => callOrder.Add("Delete"))
            .Returns(deleteMock.Object);
        mockRunner.Setup(r => r.Start(It.Is<ProcessStartInfo>(p => PsCommand(p).Contains("Register-ScheduledTask"))))
            .Callback<ProcessStartInfo>(_ => callOrder.Add("Create"))
            .Returns(createMock.Object);

        var service = new TaskSchedulerService(mockRunner.Object);

        // Act
        await service.RepairAsync();

        // Assert
        callOrder.Should().Equal("Query", "Delete", "Create");
    }

    [Fact]
    public async Task RepairAsync_WhenNotRegistered_SkipsDeleteAndCallsCreate()
    {
        // Arrange
        var callOrder = new System.Collections.Generic.List<string>();

        Mock<IProcessInstance> queryMock = CreateMockProcess(1);
        Mock<IProcessInstance> createMock = CreateMockProcess(0);

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.Is<ProcessStartInfo>(p => PsCommand(p).Contains("Get-ScheduledTask"))))
            .Callback<ProcessStartInfo>(_ => callOrder.Add("Query"))
            .Returns(queryMock.Object);
        mockRunner.Setup(r => r.Start(It.Is<ProcessStartInfo>(p => PsCommand(p).Contains("Register-ScheduledTask"))))
            .Callback<ProcessStartInfo>(_ => callOrder.Add("Create"))
            .Returns(createMock.Object);

        var service = new TaskSchedulerService(mockRunner.Object);

        // Act
        await service.RepairAsync();

        // Assert
        callOrder.Should().Equal("Query", "Create");
    }

    private static string PsCommand(ProcessStartInfo psi)
        => psi.ArgumentList.Count >= 4 ? psi.ArgumentList[3] : string.Empty;
}
