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
            .Which.Should().Be((reviewEvent, false, null));
        recorder.Logs.Should().BeEmpty();
    }

    // 保留理由は、ポップアップとバルーンのどちらでも同じ文言規則で表示できるよう素通しする（#340）
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Show_ShouldPassHoldReasonThrough(bool popupAttached)
    {
        NotificationRecorder recorder = new();
        ReviewNotificationCoordinator coordinator = recorder.CreateCoordinator();
        ReviewEvent reviewEvent = CreateReviewEvent();
        if (popupAttached)
        {
            coordinator.AttachPopup(() => recorder.Attached = true);
        }

        coordinator.Show(reviewEvent, isAutoStarted: false, holdReason: "Auto-Pause 中");

        List<(ReviewEvent ReviewEvent, bool IsAutoStarted, string? HoldReason)> target =
            popupAttached ? recorder.PopupEvents : recorder.BalloonEvents;
        target.Should().ContainSingle().Which.Should().Be((reviewEvent, false, "Auto-Pause 中"));
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
            .Which.Should().Be((reviewEvent, true, null));
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
            .Which.Should().Be((reviewEvent, false, null));
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
            .Which.Should().Be((reviewEvent, true, null));
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
            .Which.Should().Be((reviewEvent, false, null));
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

        public List<(ReviewEvent ReviewEvent, bool IsAutoStarted, string? HoldReason)> PopupEvents { get; } = [];

        public List<(ReviewEvent ReviewEvent, bool IsAutoStarted, string? HoldReason)> BalloonEvents { get; } = [];

        public List<string> Logs { get; } = [];

        public ReviewNotificationCoordinator CreateCoordinator()
            => new(
                (reviewEvent, isAutoStarted, holdReason) =>
                {
                    PopupEvents.Add((reviewEvent, isAutoStarted, holdReason));
                    if (ThrowOnPopup)
                    {
                        throw new InvalidOperationException("popup unavailable");
                    }
                },
                (reviewEvent, isAutoStarted, holdReason) => BalloonEvents.Add((reviewEvent, isAutoStarted, holdReason)),
                message =>
                {
                    Logs.Add(message);
                    return Task.CompletedTask;
                });
    }
}
