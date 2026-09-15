// <copyright file="RequestTokenBucketTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class RequestTokenBucketTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 1)]
    [InlineData(10, 4)]
    [InlineData(60, 5)]
    public void TryAcquire_ShouldAllowOnlyRefilledTokensUpToCapacity(int elapsedMinutes, int expectedAcquired)
    {
        var timeProvider = new ManualTimeProvider();
        var bucket = new RequestTokenBucket(capacity: 5, refillPerHour: 25, timeProvider);
        DrainAll(bucket).Should().Be(5);

        timeProvider.Advance(TimeSpan.FromMinutes(elapsedMinutes));

        DrainAll(bucket).Should().Be(expectedAcquired);
    }

    [Fact]
    public void AcquireWithDebt_ShouldConsumeEvenWhenEmptyAndDelayLaterAcquire()
    {
        var timeProvider = new ManualTimeProvider();
        var bucket = new RequestTokenBucket(capacity: 1, refillPerHour: 20, timeProvider);
        bucket.TryAcquire().Should().BeTrue();

        bucket.AcquireWithDebt();
        timeProvider.Advance(TimeSpan.FromMinutes(4));
        bucket.TryAcquire().Should().BeFalse();

        timeProvider.Advance(TimeSpan.FromMinutes(3));
        bucket.TryAcquire().Should().BeTrue();
    }

    private static int DrainAll(RequestTokenBucket bucket)
    {
        int acquired = 0;
        while (bucket.TryAcquire())
        {
            acquired++;
        }

        return acquired;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
