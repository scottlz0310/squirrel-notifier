// <copyright file="TrayNotificationCoordinatorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using H.NotifyIcon.Core;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class TrayNotificationCoordinatorTests
{
    [Fact]
    public void Apply_ShouldDeferErrorNotificationUntilTrayIsReady()
    {
        NotificationRecorder recorder = new();
        TrayNotificationCoordinator coordinator = recorder.CreateCoordinator();
        TrayNotificationPresentation notification = CreateNotification("接続エラー: subscriber unavailable");

        coordinator.Apply(
            SubscriptionState.Error,
            notification,
            "[UI] Showing connection error balloon notification.");

        recorder.Notifications.Should().BeEmpty();
        recorder.Logs.Should().BeEmpty();

        coordinator.MarkReady();

        recorder.Notifications.Should().ContainSingle().Which.Should().Be(notification);
        recorder.Logs.Should().ContainSingle()
            .Which.Should().Be("[UI] Showing connection error balloon notification.");
    }

    [Fact]
    public void Apply_ShouldDiscardPendingNotificationWhenStateReturnsToNormal()
    {
        NotificationRecorder recorder = new();
        TrayNotificationCoordinator coordinator = recorder.CreateCoordinator();

        coordinator.Apply(
            SubscriptionState.Error,
            CreateNotification("接続エラー"),
            "error log");
        coordinator.Apply(SubscriptionState.Running, null, null);
        coordinator.MarkReady();

        recorder.Notifications.Should().BeEmpty();
        recorder.Logs.Should().BeEmpty();
    }

    [Fact]
    public void Apply_ShouldKeepPendingNotificationForRepeatedErrorWithoutNewNotification()
    {
        NotificationRecorder recorder = new();
        TrayNotificationCoordinator coordinator = recorder.CreateCoordinator();
        TrayNotificationPresentation notification = CreateNotification("最初のエラー");

        coordinator.Apply(SubscriptionState.Error, notification, "error log");
        coordinator.Apply(SubscriptionState.Error, null, null);
        coordinator.MarkReady();

        recorder.Notifications.Should().ContainSingle().Which.Should().Be(notification);
        recorder.Logs.Should().ContainSingle().Which.Should().Be("error log");
    }

    [Fact]
    public void Apply_ShouldShowImmediatelyWhenTrayIsReady()
    {
        NotificationRecorder recorder = new();
        TrayNotificationCoordinator coordinator = recorder.CreateCoordinator();
        TrayNotificationPresentation notification = CreateNotification("認証が必要です");

        coordinator.MarkReady();
        coordinator.Apply(SubscriptionState.Error, notification, "authentication log");

        recorder.Notifications.Should().ContainSingle().Which.Should().Be(notification);
        recorder.Logs.Should().ContainSingle().Which.Should().Be("authentication log");
    }

    [Fact]
    public void Apply_ShouldLogAndContinueWhenNotificationDisplayFails()
    {
        NotificationRecorder recorder = new()
        {
            ThrowOnShow = true,
        };
        TrayNotificationCoordinator coordinator = recorder.CreateCoordinator();

        coordinator.MarkReady();
        coordinator.Apply(SubscriptionState.Error, CreateNotification("接続エラー"), "error log");

        recorder.Notifications.Should().ContainSingle();
        recorder.Logs.Should().Equal(
            "error log",
            "[UI] Failed to show tray notification: notification unavailable");
    }

    [Fact]
    public void Apply_ShouldShowOnlyLatestPendingNotification()
    {
        NotificationRecorder recorder = new();
        TrayNotificationCoordinator coordinator = recorder.CreateCoordinator();
        TrayNotificationPresentation first = CreateNotification("最初のエラー");
        TrayNotificationPresentation latest = CreateNotification("最新のエラー");

        coordinator.Apply(SubscriptionState.Error, first, "first log");
        coordinator.Apply(SubscriptionState.Error, latest, "latest log");
        coordinator.MarkReady();

        recorder.Notifications.Should().ContainSingle().Which.Should().Be(latest);
        recorder.Logs.Should().ContainSingle().Which.Should().Be("latest log");
    }

    private static TrayNotificationPresentation CreateNotification(string message)
        => new("Squirrel Notifier", message, NotificationIcon.Error);

    private sealed class NotificationRecorder
    {
        public bool ThrowOnShow { get; init; }

        public List<TrayNotificationPresentation> Notifications { get; } = [];

        public List<string> Logs { get; } = [];

        public TrayNotificationCoordinator CreateCoordinator()
            => new(
                notification =>
                {
                    Notifications.Add(notification);
                    if (ThrowOnShow)
                    {
                        throw new InvalidOperationException("notification unavailable");
                    }
                },
                message =>
                {
                    Logs.Add(message);
                    return Task.CompletedTask;
                });
    }
}
