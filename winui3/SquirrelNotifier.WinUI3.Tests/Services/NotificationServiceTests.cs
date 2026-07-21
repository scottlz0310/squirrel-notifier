// <copyright file="NotificationServiceTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class NotificationServiceTests
{
    [Fact]
    public void NotifyReviewEvent_ShouldRaiseReviewEventReceived()
    {
        var service = new NotificationService();
        var reviewEvent = new ReviewEvent
        {
            EventId = "event-1",
            Repository = "owner/repo",
            PrNumber = 181,
            PrUrl = "https://github.com/owner/repo/pull/181",
            Reason = "opened",
            Message = "レビュー対象です。",
        };
        ReviewEvent? received = null;
        service.ReviewEventReceived += (_, args) => received = args;

        service.NotifyReviewEvent(reviewEvent);

        received.Should().BeSameAs(reviewEvent);
    }

    [Fact]
    public void NotifyReviewEvent_ShouldThrow_WhenEventIsNull()
    {
        var service = new NotificationService();

        Action act = () => service.NotifyReviewEvent(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void NotifyRateLimitReset_ShouldRaiseNotificationRequested()
    {
        var service = new NotificationService();
        NotificationMessage? received = null;
        service.NotificationRequested += (_, args) => received = args;

        service.NotifyRateLimitReset("5時間制限");

        received.Should().BeEquivalentTo(new NotificationMessage(
            "レートリミット解除",
            "5時間制限 の制限が解除されました。"));
    }

    [Fact]
    public void NotifyReviewEvent_ShouldThrow_WhenNoSubscribers()
    {
        var service = new NotificationService();
        var reviewEvent = new ReviewEvent
        {
            EventId = "event-1",
            Repository = "owner/repo",
            PrNumber = 181,
            PrUrl = "https://github.com/owner/repo/pull/181",
            Reason = "opened",
            Message = "レビュー対象です。",
        };

        Action act = () => service.NotifyReviewEvent(reviewEvent);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("レビューイベントの通知先が登録されていません。");
    }

    [Fact]
    public void NotifyReviewEvent_ShouldPropagate_WhenSubscriberThrows()
    {
        var service = new NotificationService();
        var reviewEvent = new ReviewEvent
        {
            EventId = "event-1",
            Repository = "owner/repo",
            PrNumber = 181,
            PrUrl = "https://github.com/owner/repo/pull/181",
            Reason = "opened",
            Message = "レビュー対象です。",
        };
        service.ReviewEventReceived += (_, _) => throw new InvalidOperationException("配送失敗");

        Action act = () => service.NotifyReviewEvent(reviewEvent);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("配送失敗");
    }

    [Fact]
    public void NotifyRateLimitReset_ShouldNotThrow_WhenNoSubscribers()
    {
        var service = new NotificationService();

        Action act = () => service.NotifyRateLimitReset("5時間制限");

        act.Should().NotThrow();
    }
}
