// <copyright file="AutoPauseResumeSchedulerTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class AutoPauseResumeSchedulerTests
{
    private static readonly DateTimeOffset _now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(600, 630)]
    [InlineData(null, 15 * 60)]
    public void Schedule_ShouldArmTimerFromResetTime(int? resetInSeconds, int expectedSeconds)
    {
        RecordingTimeProvider timeProvider = new(_now);
        using AutoPauseResumeScheduler scheduler = new(timeProvider);

        scheduler.Schedule(resetInSeconds is int seconds ? _now.AddSeconds(seconds) : null);

        timeProvider.Timers.Should().ContainSingle()
            .Which.DueTime.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void Schedule_ShouldRaiseRetryDue_WhenTimerElapses()
    {
        RecordingTimeProvider timeProvider = new(_now);
        using AutoPauseResumeScheduler scheduler = new(timeProvider);
        int raised = 0;
        scheduler.RetryDue += (_, _) => raised++;

        scheduler.Schedule(_now.AddMinutes(1));
        timeProvider.Timers[0].Fire();

        raised.Should().Be(1);
    }

    // 保留のたびに呼ばれるため、予約は 1 本に保つ（古い予約で二重に再評価しない）
    [Fact]
    public void Schedule_ShouldReplacePreviousReservation()
    {
        RecordingTimeProvider timeProvider = new(_now);
        using AutoPauseResumeScheduler scheduler = new(timeProvider);
        int raised = 0;
        scheduler.RetryDue += (_, _) => raised++;

        scheduler.Schedule(_now.AddMinutes(1));
        scheduler.Schedule(_now.AddMinutes(2));

        timeProvider.Timers[0].IsDisposed.Should().BeTrue();
        timeProvider.Timers[1].IsDisposed.Should().BeFalse();
        timeProvider.Timers[1].Fire();
        raised.Should().Be(1);
    }

    [Fact]
    public void Schedule_ShouldDoNothing_AfterDispose()
    {
        RecordingTimeProvider timeProvider = new(_now);
        AutoPauseResumeScheduler scheduler = new(timeProvider);
        scheduler.Schedule(_now.AddMinutes(1));

        scheduler.Dispose();
        scheduler.Schedule(_now.AddMinutes(2));

        timeProvider.Timers.Should().ContainSingle().Which.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void RetryInterval_ShouldStayLongEnoughToKeepRecentActivityReadable()
    {
        // 再評価ごとに Recent activity へ行が残るため、短縮する場合は表示側の見直しが必要
        AutoPauseResumePolicy.RetryInterval.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(15));
    }

    private sealed class RecordingTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public List<RecordingTimer> Timers { get; } = [];

        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            RecordingTimer timer = new(callback, state, dueTime);
            Timers.Add(timer);
            return timer;
        }
    }

    private sealed class RecordingTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        public TimeSpan DueTime { get; private set; } = dueTime;

        public bool IsDisposed { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            DueTime = dueTime;
            return true;
        }

        public void Dispose() => IsDisposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
