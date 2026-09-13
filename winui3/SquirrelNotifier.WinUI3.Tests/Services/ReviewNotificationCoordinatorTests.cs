// <copyright file="ReviewNotificationCoordinatorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class ReviewNotificationCoordinatorTests
{
    [Fact]
    public void Show_ShouldUseBalloonBeforePopupIsAttached()
    {
        NotificationRecorder recorder = new();
        ReviewNotificationCoordinator coordinator = recorder.CreateCoordinator();
        ReviewEvent reviewEvent = CreateReviewEvent();

        coordinator.Show(reviewEvent, isAutoStarted: false);

        recorder.PopupEvents.Should().BeEmpty();
        recorder.BalloonEvents.Should().ContainSingle()
            .Which.Should().Be((reviewEvent, false));
        recorder.Logs.Should().BeEmpty();
    }

    [Fact]
    public void AttachPopup_ShouldMarkPopupAvailableWhenAttachmentSucceeds()
    {
        NotificationRecorder recorder = new();
        ReviewNotificationCoordinator coordinator = recorder.CreateCoordinator();
        ReviewEvent reviewEvent = CreateReviewEvent();

        coordinator.AttachPopup(() => recorder.Attached = true);
        coordinator.Show(reviewEvent, isAutoStarted: true);

        recorder.Attached.Should().BeTrue();
        recorder.PopupEvents.Should().ContainSingle()
            .Which.Should().Be((reviewEvent, true));
        recorder.BalloonEvents.Should().BeEmpty();
    }

    [Fact]
    public void AttachPopup_ShouldFallbackWhenAttachmentFails()
    {
        NotificationRecorder recorder = new();
        ReviewNotificationCoordinator coordinator = recorder.CreateCoordinator();
        ReviewEvent reviewEvent = CreateReviewEvent();

        coordinator.AttachPopup(() => throw new InvalidOperationException("popup unavailable"));
        coordinator.Show(reviewEvent, isAutoStarted: false);

        recorder.PopupEvents.Should().BeEmpty();
        recorder.BalloonEvents.Should().ContainSingle()
            .Which.Should().Be((reviewEvent, false));
        recorder.Logs.Should().ContainSingle()
            .Which.Should().Contain("Failed to attach tray popup: popup unavailable");
    }

    [Fact]
    public void Show_ShouldFallbackAndLogWhenPopupFails()
    {
        NotificationRecorder recorder = new()
        {
            ThrowOnPopup = true,
        };
        ReviewNotificationCoordinator coordinator = recorder.CreateCoordinator();
        ReviewEvent reviewEvent = CreateReviewEvent();
        coordinator.AttachPopup(() => recorder.Attached = true);

        coordinator.Show(reviewEvent, isAutoStarted: true);

        recorder.PopupEvents.Should().ContainSingle();
        recorder.BalloonEvents.Should().ContainSingle()
            .Which.Should().Be((reviewEvent, true));
        recorder.Logs.Should().ContainSingle()
            .Which.Should().Contain("Failed to show review popup: popup unavailable");
    }

    [Fact]
    public void ShowFallback_ShouldAlwaysUseBalloon()
    {
        NotificationRecorder recorder = new();
        ReviewNotificationCoordinator coordinator = recorder.CreateCoordinator();
        ReviewEvent reviewEvent = CreateReviewEvent();
        coordinator.AttachPopup(() => recorder.Attached = true);

        coordinator.ShowFallback(reviewEvent, isAutoStarted: false);

        recorder.PopupEvents.Should().BeEmpty();
        recorder.BalloonEvents.Should().ContainSingle()
            .Which.Should().Be((reviewEvent, false));
    }

    private static ReviewEvent CreateReviewEvent()
        => new()
        {
            Repository = "scottlz0310/squirrel-notifier",
            PrNumber = 310,
            Reason = "opened",
        };

    private sealed class NotificationRecorder
    {
        public bool Attached { get; set; }

        public bool ThrowOnPopup { get; init; }

        public List<(ReviewEvent ReviewEvent, bool IsAutoStarted)> PopupEvents { get; } = [];

        public List<(ReviewEvent ReviewEvent, bool IsAutoStarted)> BalloonEvents { get; } = [];

        public List<string> Logs { get; } = [];

        public ReviewNotificationCoordinator CreateCoordinator()
            => new(
                (reviewEvent, isAutoStarted) =>
                {
                    PopupEvents.Add((reviewEvent, isAutoStarted));
                    if (ThrowOnPopup)
                    {
                        throw new InvalidOperationException("popup unavailable");
                    }
                },
                (reviewEvent, isAutoStarted) => BalloonEvents.Add((reviewEvent, isAutoStarted)),
                message =>
                {
                    Logs.Add(message);
                    return Task.CompletedTask;
                });
    }
}
