// <copyright file="ReviewNotificationFormatterTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public sealed class ReviewNotificationFormatterTests
{
    // 保留（#339/#340）は、自動起動しなかっただけのイベントと区別できる文言にする。
    // 起動した場合の注記（CI 待機の上限に達して起動した、など、#456）は、起動したことを保ったまま添える
    [Theory]
    [InlineData(true, "opened", null, "自動でレビューを開始しました: scottlz0310/squirrel-notifier#292")]
    [InlineData(false, "opened", null, "opened: scottlz0310/squirrel-notifier#292")]
    [InlineData(false, "review-posted", null, "review-posted: scottlz0310/squirrel-notifier#292")]
    [InlineData(false, "opened", "Auto-Pause 中", "レビューを保留中（Auto-Pause 中）: scottlz0310/squirrel-notifier#292")]
    [InlineData(false, "opened", "別のレビューが実行中", "レビューを保留中（別のレビューが実行中）: scottlz0310/squirrel-notifier#292")]
    [InlineData(false, "opened", "CI 完了待ち", "レビューを保留中（CI 完了待ち）: scottlz0310/squirrel-notifier#292")]
    [InlineData(true, "opened", "CI 待機の上限に達したため起動", "自動でレビューを開始しました（CI 待機の上限に達したため起動）: scottlz0310/squirrel-notifier#292")]
    public void BuildSummary_ShouldDescribeReviewEvent(
        bool isAutoStarted,
        string reason,
        string? statusNote,
        string expected)
    {
        var reviewEvent = new ReviewEvent
        {
            Repository = "scottlz0310/squirrel-notifier",
            PrNumber = 292,
            Reason = reason,
        };

        string result = ReviewNotificationFormatter.BuildSummary(reviewEvent, isAutoStarted, statusNote);

        result.Should().Be(expected);
    }
}
