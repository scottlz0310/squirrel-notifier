// <copyright file="ProducerEpochStoreTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class ProducerEpochStoreTests : IDisposable
{
    private static readonly DateTimeOffset _now = new(2026, 10, 25, 3, 0, 0, TimeSpan.Zero);
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"ProducerEpochStoreTests_{Guid.NewGuid():D}");

    public ProducerEpochStoreTests()
    {
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public void AcquireNext_ShouldStartFromTheCurrentUnixTime()
    {
        new ProducerEpochStore(_testDirectory, new FixedTime(_now)).AcquireNext().Should().Be(_now.ToUnixTimeSeconds());
    }

    [Fact]
    public void AcquireNext_ShouldIncreaseOnEveryStart_EvenWithinTheSameSecond()
    {
        FixedTime time = new(_now);

        long first = new ProducerEpochStore(_testDirectory, time).AcquireNext();
        long second = new ProducerEpochStore(_testDirectory, time).AcquireNext();
        long third = new ProducerEpochStore(_testDirectory, time).AcquireNext();

        second.Should().Be(first + 1);
        third.Should().Be(second + 1);
    }

    [Fact]
    public void AcquireNext_ShouldNeverDecrease_WhenTheClockMovesBackwards()
    {
        long before = new ProducerEpochStore(_testDirectory, new FixedTime(_now)).AcquireNext();

        long after = new ProducerEpochStore(_testDirectory, new FixedTime(_now.AddDays(-1))).AcquireNext();

        after.Should().BeGreaterThan(before);
    }

    [Fact]
    public void AcquireNext_ShouldFallBackToTheCurrentTime_WhenTheRecordIsCorrupt()
    {
        File.WriteAllText(Path.Combine(_testDirectory, ProducerEpochStore.FileName), "{ not json");

        new ProducerEpochStore(_testDirectory, new FixedTime(_now)).AcquireNext().Should().Be(_now.ToUnixTimeSeconds());
    }

    [Fact]
    public void AcquireNext_ShouldStillReturnAnEpoch_WhenTheRecordCannotBeWritten()
    {
        // 記録のパスをディレクトリにして、書き込みを失敗させる
        Directory.CreateDirectory(Path.Combine(_testDirectory, ProducerEpochStore.FileName));

        Func<long> act = () => new ProducerEpochStore(_testDirectory, new FixedTime(_now)).AcquireNext();

        act.Should().NotThrow().Which.Should().Be(_now.ToUnixTimeSeconds());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_ShouldRejectAnEmptyDirectory(string directory)
    {
        Action act = () => _ = new ProducerEpochStore(directory);

        act.Should().Throw<ArgumentException>();
    }

    private sealed class FixedTime(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
