// <copyright file="RateLimitReminderServiceTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using Moq;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class RateLimitReminderServiceTests
{
    [Fact]
    public void Schedule_ShouldMarkAsScheduled()
    {
        var mockNotificationService = new Mock<INotificationService>();
        using var service = new RateLimitReminderService(mockNotificationService.Object);

        service.Schedule("5h", "5時間制限", DateTimeOffset.Now.AddMinutes(5));

        service.IsScheduled("5h").Should().BeTrue();
    }

    [Fact]
    public void Cancel_ShouldUnmarkAsScheduled()
    {
        var mockNotificationService = new Mock<INotificationService>();
        using var service = new RateLimitReminderService(mockNotificationService.Object);
        service.Schedule("5h", "5時間制限", DateTimeOffset.Now.AddMinutes(5));

        service.Cancel("5h");

        service.IsScheduled("5h").Should().BeFalse();
    }

    [Fact]
    public void Cancel_UnknownId_ShouldNotThrow()
    {
        var mockNotificationService = new Mock<INotificationService>();
        using var service = new RateLimitReminderService(mockNotificationService.Object);

        Action act = () => service.Cancel("unknown");

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Schedule_WhenTimeElapses_ShouldNotifyAndUnmarkAsScheduled()
    {
        var mockNotificationService = new Mock<INotificationService>();
        using var service = new RateLimitReminderService(mockNotificationService.Object);

        service.Schedule("5h", "5時間制限", DateTimeOffset.Now.AddMilliseconds(50));

        await Task.Delay(500);

        mockNotificationService.Verify(n => n.NotifyRateLimitReset("5時間制限"), Times.Once);
        service.IsScheduled("5h").Should().BeFalse();
    }

    [Fact]
    public async Task Schedule_CalledTwiceForSameId_ShouldCancelPreviousReminder()
    {
        var mockNotificationService = new Mock<INotificationService>();
        using var service = new RateLimitReminderService(mockNotificationService.Object);

        service.Schedule("5h", "旧ラベル", DateTimeOffset.Now.AddMilliseconds(50));
        service.Schedule("5h", "新ラベル", DateTimeOffset.Now.AddMilliseconds(50));

        await Task.Delay(500);

        mockNotificationService.Verify(n => n.NotifyRateLimitReset("旧ラベル"), Times.Never);
        mockNotificationService.Verify(n => n.NotifyRateLimitReset("新ラベル"), Times.Once);
    }

    [Fact]
    public async Task Dispose_ShouldCancelAllPendingReminders()
    {
        var mockNotificationService = new Mock<INotificationService>();
        var service = new RateLimitReminderService(mockNotificationService.Object);
        service.Schedule("5h", "5時間制限", DateTimeOffset.Now.AddMilliseconds(50));

        service.Dispose();
        await Task.Delay(500);

        mockNotificationService.Verify(n => n.NotifyRateLimitReset(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Schedule_WithPastResetAt_ShouldNotifyImmediately()
    {
        var mockNotificationService = new Mock<INotificationService>();
        using var service = new RateLimitReminderService(mockNotificationService.Object);

        service.Schedule("5h", "5時間制限", DateTimeOffset.Now.AddMinutes(-5));

        await Task.Delay(200);

        mockNotificationService.Verify(n => n.NotifyRateLimitReset("5時間制限"), Times.Once);
    }

    [Fact]
    public async Task Schedule_WhenTimeElapses_ShouldRaiseReminderFiredWithId()
    {
        var mockNotificationService = new Mock<INotificationService>();
        using var service = new RateLimitReminderService(mockNotificationService.Object);
        List<string> firedIds = new();
        service.ReminderFired += (_, id) => firedIds.Add(id);

        service.Schedule("5h", "5時間制限", DateTimeOffset.Now.AddMilliseconds(50));

        await Task.Delay(500);

        firedIds.Should().ContainSingle().Which.Should().Be("5h");
    }

    [Fact]
    public void Cancel_ShouldNotRaiseReminderFired()
    {
        var mockNotificationService = new Mock<INotificationService>();
        using var service = new RateLimitReminderService(mockNotificationService.Object);
        bool raised = false;
        service.ReminderFired += (_, _) => raised = true;
        service.Schedule("5h", "5時間制限", DateTimeOffset.Now.AddMinutes(5));

        service.Cancel("5h");

        raised.Should().BeFalse();
    }
}
