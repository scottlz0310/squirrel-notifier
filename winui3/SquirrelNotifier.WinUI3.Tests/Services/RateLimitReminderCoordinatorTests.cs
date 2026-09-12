// <copyright file="RateLimitReminderCoordinatorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using Moq;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class RateLimitReminderCoordinatorTests
{
    [Fact]
    public void Toggle_WhenReminderIsNotScheduled_ShouldScheduleAndMarkAsScheduled()
    {
        Mock<IRateLimitReminderService> reminderService = new();
        RateLimitInfo info = CreateInfo();
        RateLimitReminderCoordinator coordinator = new(reminderService.Object);

        coordinator.Toggle(info);

        info.IsReminderScheduled.Should().BeTrue();
        reminderService.Verify(
            service => service.Schedule(info.ReminderKey, info.Label, info.ResetAt),
            Times.Once);
        reminderService.Verify(service => service.Cancel(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Toggle_WhenReminderIsScheduled_ShouldCancelAndMarkAsNotScheduled()
    {
        Mock<IRateLimitReminderService> reminderService = new();
        RateLimitInfo info = CreateInfo();
        info.IsReminderScheduled = true;
        RateLimitReminderCoordinator coordinator = new(reminderService.Object);

        coordinator.Toggle(info);

        info.IsReminderScheduled.Should().BeFalse();
        reminderService.Verify(service => service.Cancel(info.ReminderKey), Times.Once);
        reminderService.Verify(
            service => service.Schedule(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>()),
            Times.Never);
    }

    private static RateLimitInfo CreateInfo()
        => new()
        {
            Id = "five-hour",
            Label = "5時間制限",
            ResetAt = new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero),
            SourceUri = "agent://codex",
        };
}
