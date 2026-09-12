// <copyright file="ReviewNotificationFormatterTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public sealed class ReviewNotificationFormatterTests
{
    [Theory]
    [InlineData(true, "opened", "自動でレビューを開始しました: scottlz0310/squirrel-notifier#292")]
    [InlineData(false, "opened", "opened: scottlz0310/squirrel-notifier#292")]
    [InlineData(false, "review-posted", "review-posted: scottlz0310/squirrel-notifier#292")]
    public void BuildSummary_ShouldDescribeReviewEvent(
        bool isAutoStarted,
        string reason,
        string expected)
    {
        var reviewEvent = new ReviewEvent
        {
            Repository = "scottlz0310/squirrel-notifier",
            PrNumber = 292,
            Reason = reason,
        };

        string result = ReviewNotificationFormatter.BuildSummary(reviewEvent, isAutoStarted);

        result.Should().Be(expected);
    }
}
