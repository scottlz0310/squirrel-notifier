// <copyright file="AutoStartCoordinatorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class AutoStartCoordinatorTests
{
    [Theory]
    [InlineData("Enabled", true, "有効", true, false)]
    [InlineData("EnabledByPolicy", true, "有効（管理者のポリシー）", false, false)]
    [InlineData("Disabled", false, "無効", false, true)]
    [InlineData("DisabledByUser", false, "無効（Windows の設定）", false, false)]
    [InlineData("DisabledByPolicy", false, "無効（管理者のポリシー）", false, false)]
    [InlineData("Invalid", false, "要修復", true, false)]
    [InlineData("CheckFailed", null, "確認に失敗しました", false, false)]
    public void DescribeStatus_ShouldMapTaskStatusToPresentation(
        string statusName,
        bool? expectedToggleIsOn,
        string expectedStatusText,
        bool expectedRepairEnabled,
        bool expectedOnboardingOpen)
    {
        AutoStartStatus status = Enum.Parse<AutoStartStatus>(statusName);
        AutoStartStatusPresentation presentation = AutoStartCoordinator.DescribeStatus(status);

        presentation.ToggleIsOn.Should().Be(expectedToggleIsOn);
        presentation.StatusText.Should().Be(expectedStatusText);
        presentation.IsRepairEnabled.Should().Be(expectedRepairEnabled);
        presentation.IsOnboardingOpen.Should().Be(expectedOnboardingOpen);
    }

    [Fact]
    public void DescribeStatus_ShouldRejectUnknownStatus()
    {
        Action action = () => AutoStartCoordinator.DescribeStatus((AutoStartStatus)999);

        action.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("status");
    }

    [Fact]
    public async Task RefreshStatusAsync_ShouldApplyPresentationWhileSuppressingToggle()
    {
        var scheduler = new Mock<IRepairableAutoStartService>();
        scheduler.Setup(service => service.GetStatusAsync())
            .ReturnsAsync(AutoStartStatus.Enabled);
        var coordinator = new AutoStartCoordinator(scheduler.Object);
        AutoStartStatusPresentation? actual = null;
        bool wasSuppressed = false;

        await coordinator.RefreshStatusAsync(presentation =>
        {
            actual = presentation;
            wasSuppressed = coordinator.IsToggleSuppressed;
        });

        actual.Should().NotBeNull();
        actual!.StatusText.Should().Be("有効");
        wasSuppressed.Should().BeTrue();
        coordinator.IsToggleSuppressed.Should().BeFalse();
    }

    [Fact]
    public async Task RefreshStatusAsync_ShouldReturnCheckFailedPresentationWhenStatusThrows()
    {
        var scheduler = new Mock<IRepairableAutoStartService>();
        scheduler.Setup(service => service.GetStatusAsync())
            .ThrowsAsync(new InvalidOperationException("状態取得失敗"));
        var coordinator = new AutoStartCoordinator(scheduler.Object);
        AutoStartStatusPresentation? actual = null;

        await coordinator.RefreshStatusAsync(presentation => actual = presentation);

        actual.Should().NotBeNull();
        actual!.ToggleIsOn.Should().BeNull();
        actual.StatusText.Should().Be("確認に失敗しました");
    }

    [Fact]
    public async Task RefreshStatusAsync_ShouldSkipWhileOperationIsPending()
    {
        var scheduler = new Mock<IRepairableAutoStartService>();
        var coordinator = new AutoStartCoordinator(scheduler.Object);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<AutoStartOperationResult> operation = coordinator.ToggleAsync(
            true,
            _ =>
            {
                entered.SetResult(true);
                return confirmation.Task;
            });
        await entered.Task;

        bool applied = false;
        await coordinator.RefreshStatusAsync(_ => applied = true);

        applied.Should().BeFalse();
        scheduler.Verify(service => service.GetStatusAsync(), Times.Never);
        confirmation.SetResult(false);
        await operation;
    }

    [Fact]
    public async Task RefreshStatusAsync_ShouldSkipReentrantRefreshWhileApplyingPresentation()
    {
        var scheduler = new Mock<IRepairableAutoStartService>();
        scheduler.Setup(service => service.GetStatusAsync())
            .ReturnsAsync(AutoStartStatus.Enabled);
        var coordinator = new AutoStartCoordinator(scheduler.Object);
        Task? nestedRefresh = null;

        await coordinator.RefreshStatusAsync(_ =>
        {
            nestedRefresh = coordinator.RefreshStatusAsync(_ => { });
        });

        await nestedRefresh!;
        scheduler.Verify(service => service.GetStatusAsync(), Times.Once);
    }

    [Fact]
    public async Task ToggleAsync_ShouldConfirmAndRegisterWhileKeepingOperationPending()
    {
        var scheduler = new Mock<IRepairableAutoStartService>();
        scheduler.SetupGet(service => service.EnableConfirmationMessage).Returns("有効化の確認");
        var coordinator = new AutoStartCoordinator(scheduler.Object);
        AutoStartConfirmation? actualConfirmation = null;
        bool wasSuppressed = false;

        AutoStartOperationResult result = await coordinator.ToggleAsync(
            true,
            confirmation =>
            {
                actualConfirmation = confirmation;
                wasSuppressed = coordinator.IsToggleSuppressed;
                return Task.FromResult(true);
            });

        result.Status.Should().Be(AutoStartOperationStatus.Completed);
        result.ShouldRefresh.Should().BeTrue();
        actualConfirmation.Should().NotBeNull();
        actualConfirmation!.Title.Should().Be("自動起動を設定します");
        actualConfirmation.Message.Should().Be("有効化の確認");
        wasSuppressed.Should().BeTrue();
        coordinator.IsToggleSuppressed.Should().BeFalse();
        scheduler.Verify(service => service.EnableAsync(), Times.Once);
    }

    [Fact]
    public async Task ToggleAsync_ShouldConfirmAndUnregisterWhenDisabled()
    {
        var scheduler = new Mock<IRepairableAutoStartService>();
        scheduler.SetupGet(service => service.DisableConfirmationMessage).Returns("無効化の確認");
        var coordinator = new AutoStartCoordinator(scheduler.Object);
        AutoStartConfirmation? actualConfirmation = null;

        AutoStartOperationResult result = await coordinator.ToggleAsync(
            false,
            confirmation =>
            {
                actualConfirmation = confirmation;
                return Task.FromResult(true);
            });

        result.Status.Should().Be(AutoStartOperationStatus.Completed);
        actualConfirmation.Should().NotBeNull();
        actualConfirmation!.Title.Should().Be("自動起動を解除します");
        actualConfirmation.Message.Should().Be("無効化の確認");
        scheduler.Verify(service => service.DisableAsync(), Times.Once);
    }

    [Fact]
    public async Task ToggleAsync_ShouldRestoreToggleAndSkipOperationWhenCancelled()
    {
        var scheduler = new Mock<IRepairableAutoStartService>();
        var coordinator = new AutoStartCoordinator(scheduler.Object);

        AutoStartOperationResult result = await coordinator.ToggleAsync(
            false,
            _ => Task.FromResult(false));

        result.Status.Should().Be(AutoStartOperationStatus.CancelledByUser);
        result.ToggleIsOn.Should().BeTrue();
        result.ShouldRefresh.Should().BeFalse();
        scheduler.Verify(service => service.DisableAsync(), Times.Never);
    }

    [Fact]
    public async Task ToggleAsync_ShouldReturnFailureAndResetOperationStateWhenOperationThrows()
    {
        var scheduler = new Mock<IRepairableAutoStartService>();
        scheduler.Setup(service => service.EnableAsync())
            .ThrowsAsync(new InvalidOperationException("登録失敗"));
        var coordinator = new AutoStartCoordinator(scheduler.Object);

        AutoStartOperationResult result = await coordinator.ToggleAsync(true, _ => Task.FromResult(true));

        result.Status.Should().Be(AutoStartOperationStatus.Failed);
        result.ErrorTitle.Should().Be("自動起動の設定に失敗しました");
        result.ErrorMessage.Should().Be("登録失敗");
        result.ShouldRefresh.Should().BeTrue();
        coordinator.IsToggleSuppressed.Should().BeFalse();
    }

    [Fact]
    public async Task ToggleAsync_ShouldSkipReentrantOperationDuringConfirmation()
    {
        var scheduler = new Mock<IRepairableAutoStartService>();
        var coordinator = new AutoStartCoordinator(scheduler.Object);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<AutoStartOperationResult> first = coordinator.ToggleAsync(
            true,
            _ =>
            {
                entered.SetResult(true);
                return confirmation.Task;
            });
        await entered.Task;

        AutoStartOperationResult second = await coordinator.ToggleAsync(true, _ => Task.FromResult(true));

        second.Status.Should().Be(AutoStartOperationStatus.SkippedBusy);
        confirmation.SetResult(false);
        (await first).Status.Should().Be(AutoStartOperationStatus.CancelledByUser);
    }

    [Fact]
    public async Task RepairAsync_ShouldCompleteAndRequestRefresh()
    {
        var scheduler = new Mock<IRepairableAutoStartService>();
        var coordinator = new AutoStartCoordinator(scheduler.Object);

        AutoStartOperationResult result = await coordinator.RepairAsync();

        result.Status.Should().Be(AutoStartOperationStatus.Completed);
        result.ShouldRefresh.Should().BeTrue();
        coordinator.IsToggleSuppressed.Should().BeFalse();
        scheduler.Verify(service => service.RepairAsync(), Times.Once);
    }

    [Fact]
    public async Task RepairAsync_ShouldSkipWhileAnotherOperationIsPending()
    {
        var scheduler = new Mock<IRepairableAutoStartService>();
        var coordinator = new AutoStartCoordinator(scheduler.Object);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<AutoStartOperationResult> operation = coordinator.ToggleAsync(
            true,
            _ =>
            {
                entered.SetResult(true);
                return confirmation.Task;
            });
        await entered.Task;

        AutoStartOperationResult result = await coordinator.RepairAsync();

        result.Status.Should().Be(AutoStartOperationStatus.SkippedBusy);
        scheduler.Verify(service => service.RepairAsync(), Times.Never);
        confirmation.SetResult(false);
        await operation;
    }

    [Fact]
    public async Task RepairAsync_ShouldReturnFailureAndResetOperationStateWhenRepairThrows()
    {
        var scheduler = new Mock<IRepairableAutoStartService>();
        scheduler.Setup(service => service.RepairAsync())
            .ThrowsAsync(new InvalidOperationException("修復失敗"));
        var coordinator = new AutoStartCoordinator(scheduler.Object);

        AutoStartOperationResult result = await coordinator.RepairAsync();

        result.Status.Should().Be(AutoStartOperationStatus.Failed);
        result.ErrorTitle.Should().Be("自動起動の修復に失敗しました");
        result.ErrorMessage.Should().Be("修復失敗");
        coordinator.IsToggleSuppressed.Should().BeFalse();
    }

    [Fact]
    public async Task RefreshStatusAsync_ShouldDisableRepairForNonRepairableService()
    {
        var service = new Mock<IAutoStartService>();
        service.Setup(autoStart => autoStart.GetStatusAsync()).ReturnsAsync(AutoStartStatus.Enabled);
        var coordinator = new AutoStartCoordinator(service.Object);
        AutoStartStatusPresentation? actual = null;

        await coordinator.RefreshStatusAsync(presentation => actual = presentation);

        actual!.ToggleIsOn.Should().BeTrue();
        actual.IsRepairEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task RepairAsync_ShouldFailForNonRepairableService()
    {
        var coordinator = new AutoStartCoordinator(new Mock<IAutoStartService>().Object);

        AutoStartOperationResult result = await coordinator.RepairAsync();

        result.Status.Should().Be(AutoStartOperationStatus.Failed);
        result.ErrorMessage.Should().Contain("修復の対象ではありません");
    }
}
