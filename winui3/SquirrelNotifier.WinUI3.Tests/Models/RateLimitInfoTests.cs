// <copyright file="RateLimitInfoTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.ComponentModel;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Models;

public class RateLimitInfoTests
{
    [Fact]
    public void ReminderKey_ShouldCombineSourceUriAndId()
    {
        var info = new RateLimitInfo { Id = "5h", SourceUri = "ratelimit://status/claude" };

        info.ReminderKey.Should().Be("ratelimit://status/claude:5h");
    }

    [Fact]
    public void ReminderButtonText_WhenNotScheduled_ShouldShowScheduleLabel()
    {
        var info = new RateLimitInfo();

        info.ReminderButtonText.Should().Be("通知予約 ⏰");
    }

    [Fact]
    public void ReminderButtonText_WhenScheduled_ShouldShowScheduledLabel()
    {
        var info = new RateLimitInfo { IsReminderScheduled = true };

        info.ReminderButtonText.Should().Be("予約済み ⏰ (解除)");
    }

    [Fact]
    public void IsReminderScheduled_WhenChanged_ShouldRaisePropertyChangedForBothProperties()
    {
        var info = new RateLimitInfo();
        List<string?> raisedProperties = new();
        info.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        info.IsReminderScheduled = true;

        raisedProperties.Should().Contain(nameof(RateLimitInfo.IsReminderScheduled));
        raisedProperties.Should().Contain(nameof(RateLimitInfo.ReminderButtonText));
    }

    [Fact]
    public void IsReminderScheduled_WhenSetToSameValue_ShouldNotRaisePropertyChanged()
    {
        var info = new RateLimitInfo();
        bool raised = false;
        info.PropertyChanged += (_, _) => raised = true;

        info.IsReminderScheduled = false;

        raised.Should().BeFalse();
    }

    [Fact]
    public void ResetAtDisplay_ShouldFormatAsLocalDateTime()
    {
        var resetAt = new DateTimeOffset(2026, 7, 5, 20, 0, 0, TimeSpan.Zero);
        var info = new RateLimitInfo { ResetAt = resetAt };

        info.ResetAtDisplay.Should().Be(resetAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm", System.Globalization.CultureInfo.CurrentCulture));
    }

    [Fact]
    public void Validate_WithValidData_ShouldNotThrow()
    {
        var info = new RateLimitInfo { Id = "5h", Label = "5時間制限", ResetAt = DateTimeOffset.Now };

        Action act = info.Validate;

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("", "label")]
    [InlineData(" ", "label")]
    public void Validate_WithEmptyId_ShouldThrow(string id, string label)
    {
        var info = new RateLimitInfo { Id = id, Label = label, ResetAt = DateTimeOffset.Now };

        Action act = info.Validate;

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("5h", "")]
    [InlineData("5h", " ")]
    public void Validate_WithEmptyLabel_ShouldThrow(string id, string label)
    {
        var info = new RateLimitInfo { Id = id, Label = label, ResetAt = DateTimeOffset.Now };

        Action act = info.Validate;

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validate_WithDefaultResetAt_ShouldThrow()
    {
        var info = new RateLimitInfo { Id = "5h", Label = "5時間制限" };

        Action act = info.Validate;

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validate_WithNullUsedPercentage_ShouldNotThrow()
    {
        var info = new RateLimitInfo { Id = "5h", Label = "5時間制限", ResetAt = DateTimeOffset.Now, UsedPercentage = null };

        Action act = info.Validate;

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(73.5)]
    [InlineData(100)]
    public void Validate_WithValidUsedPercentage_ShouldNotThrow(double usedPercentage)
    {
        var info = new RateLimitInfo { Id = "5h", Label = "5時間制限", ResetAt = DateTimeOffset.Now, UsedPercentage = usedPercentage };

        Action act = info.Validate;

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(-100)]
    [InlineData(100.1)]
    [InlineData(1000)]
    public void Validate_WithOutOfRangeUsedPercentage_ShouldThrow(double usedPercentage)
    {
        var info = new RateLimitInfo { Id = "5h", Label = "5時間制限", ResetAt = DateTimeOffset.Now, UsedPercentage = usedPercentage };

        Action act = info.Validate;

        act.Should().Throw<ArgumentException>();
    }
}
