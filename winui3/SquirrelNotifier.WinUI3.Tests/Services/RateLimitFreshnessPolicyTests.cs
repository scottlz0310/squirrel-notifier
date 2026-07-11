// <copyright file="RateLimitFreshnessPolicyTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class RateLimitFreshnessPolicyTests
{
    private static readonly DateTimeOffset _now = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan _threshold = TimeSpan.FromMinutes(15);

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(15)]
    public void IsFresh_WithinThreshold_ShouldReturnTrue(int minutesAgo)
    {
        DateTimeOffset observedAt = _now - TimeSpan.FromMinutes(minutesAgo);

        RateLimitFreshnessPolicy.IsFresh(observedAt, _now, _threshold).Should().BeTrue();
    }

    [Theory]
    [InlineData(15.1)]
    [InlineData(30)]
    [InlineData(1440)]
    public void IsFresh_BeyondThreshold_ShouldReturnFalse(double minutesAgo)
    {
        DateTimeOffset observedAt = _now - TimeSpan.FromMinutes(minutesAgo);

        RateLimitFreshnessPolicy.IsFresh(observedAt, _now, _threshold).Should().BeFalse();
    }

    [Fact]
    public void IsFresh_FutureTimestamp_ShouldReturnFalse()
    {
        DateTimeOffset observedAt = _now + TimeSpan.FromMinutes(1);

        RateLimitFreshnessPolicy.IsFresh(observedAt, _now, _threshold).Should().BeFalse();
    }

    [Fact]
    public void IsFresh_ExactlyNow_ShouldReturnTrue()
    {
        RateLimitFreshnessPolicy.IsFresh(_now, _now, _threshold).Should().BeTrue();
    }
}
