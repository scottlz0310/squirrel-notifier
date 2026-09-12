// <copyright file="SubscriptionStateCoordinatorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using H.NotifyIcon.Core;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class SubscriptionStateCoordinatorTests
{
    [Theory]
    [InlineData("Stopped")]
    [InlineData("Starting")]
    [InlineData("Running")]
    [InlineData("Stopping")]
    public void Update_ShouldReturnNormalPresentation_ForNonErrorStates(string stateName)
    {
        SubscriptionStateCoordinator coordinator = new();

        SubscriptionStatePresentation presentation = coordinator.Update(
            Enum.Parse<SubscriptionState>(stateName),
            "ignored error",
            isAuthenticationRequired: true);

        presentation.IconFileName.Should().Be("squirrel-notifier.ico");
        presentation.Tooltip.Should().Be("Squirrel Notifier");
        presentation.StatusText.Should().BeNull();
        presentation.IsAuthenticationRequired.Should().BeFalse();
        presentation.Notification.Should().BeNull();
        presentation.StateLogMessage.Should().Be($"[UI] Updating tray icon to normal state. State: {stateName}");
        presentation.NotificationLogMessage.Should().BeNull();
    }

    [Theory]
    [InlineData(true, "認証が必要です", "認証が必要です", "[UI] Showing authentication required balloon notification.")]
    [InlineData(false, "接続に失敗しました", "接続エラー: 接続に失敗しました", "[UI] Showing connection error balloon notification.")]
    public void Update_ShouldPresentFirstErrorWithAuthenticationSpecificNotification(
        bool isAuthenticationRequired,
        string lastError,
        string expectedNotificationMessage,
        string expectedNotificationLogMessage)
    {
        SubscriptionStateCoordinator coordinator = new();

        SubscriptionStatePresentation presentation = coordinator.Update(
            SubscriptionState.Error,
            lastError,
            isAuthenticationRequired);

        presentation.IconFileName.Should().Be("squirrel-notifier-error.ico");
        presentation.Tooltip.Should().Be($"Squirrel Notifier - Error: {lastError}");
        presentation.StatusText.Should().Be($"Error: {lastError}");
        presentation.IsAuthenticationRequired.Should().Be(isAuthenticationRequired);
        presentation.StateLogMessage.Should().Be($"[UI] Updating tray icon to error state. Error: {lastError}");
        presentation.NotificationLogMessage.Should().Be(expectedNotificationLogMessage);
        presentation.Notification.Should().BeEquivalentTo(new TrayNotificationPresentation(
            "Squirrel Notifier",
            expectedNotificationMessage,
            NotificationIcon.Error));
    }

    [Fact]
    public void Update_ShouldNotifyEmptyErrorOnce()
    {
        SubscriptionStateCoordinator coordinator = new();

        SubscriptionStatePresentation presentation = coordinator.Update(
            SubscriptionState.Error,
            string.Empty,
            isAuthenticationRequired: false);

        presentation.StatusText.Should().BeNull();
        presentation.Notification.Should().NotBeNull();
        presentation.Notification!.Message.Should().Be("接続エラー: ");
    }

    [Fact]
    public void Update_ShouldSuppressRepeatedErrorsUntilNormalStateIsReported()
    {
        SubscriptionStateCoordinator coordinator = new();

        SubscriptionStatePresentation firstError = coordinator.Update(
            SubscriptionState.Error,
            "最初のエラー",
            isAuthenticationRequired: false);
        SubscriptionStatePresentation repeatedError = coordinator.Update(
            SubscriptionState.Error,
            "同じエラー",
            isAuthenticationRequired: false);
        SubscriptionStatePresentation normal = coordinator.Update(
            SubscriptionState.Running,
            string.Empty,
            isAuthenticationRequired: false);
        SubscriptionStatePresentation nextError = coordinator.Update(
            SubscriptionState.Error,
            "復帰後のエラー",
            isAuthenticationRequired: false);

        firstError.Notification.Should().NotBeNull();
        repeatedError.Notification.Should().BeNull();
        repeatedError.NotificationLogMessage.Should().BeNull();
        normal.Notification.Should().BeNull();
        nextError.Notification.Should().NotBeNull();
    }
}
