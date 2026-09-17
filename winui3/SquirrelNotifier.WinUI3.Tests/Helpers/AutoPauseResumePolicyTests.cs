// <copyright file="AutoPauseResumePolicyTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public sealed class AutoPauseResumePolicyTests
{
    private static readonly DateTimeOffset _now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    // リセット時刻の 30 秒後に評価する。取得不可・通過済みは、次の snapshot 更新を待つ間隔へ落とす
    [Theory]
    [InlineData(null, 15 * 60)]
    [InlineData(120, 150)]
    [InlineData(5 * 3600, (5 * 3600) + 30)]
    [InlineData(0, 30)]
    [InlineData(-30, 15 * 60)]
    [InlineData(-3600, 15 * 60)]
    public void ResolveRetryDelay_ShouldFollowResetTime(int? resetInSeconds, int expectedSeconds)
    {
        DateTimeOffset? resetAt = resetInSeconds is int seconds ? _now.AddSeconds(seconds) : null;

        TimeSpan delay = AutoPauseResumePolicy.ResolveRetryDelay(resetAt, _now);

        delay.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void ResolveRetryDelay_ShouldClampAbsurdResetTime()
    {
        TimeSpan delay = AutoPauseResumePolicy.ResolveRetryDelay(_now.AddYears(1), _now);

        delay.Should().Be(TimeSpan.FromMilliseconds(int.MaxValue - 1));
    }
}
