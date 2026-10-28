// <copyright file="LauncherWatchdogTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Threading.Channels;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class LauncherWatchdogTests
{
    [Fact]
    public void Timer_ShouldReportOnlyOnceAndStopAfterDisposal()
    {
        var clock = new ManualClock();
        var errors = new List<LauncherWatchdogTimeout>();
        using (var watchdog = new LauncherWatchdog(clock, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), errors.Add))
        {
            clock.Advance(TimeSpan.FromMinutes(9));
            watchdog.Observe("""{"type":"item.started","item":{"type":"command_execution","id":"a"}}""", LauncherOutputFormat.CodexJson);
            clock.Advance(TimeSpan.FromMinutes(29));
            clock.Fire();
            errors.Should().BeEmpty();
            clock.Advance(TimeSpan.FromMinutes(1));
            clock.Fire();
            clock.Fire();
            errors.Should().Equal(LauncherWatchdogTimeout.Tool);
        }

        clock.Fire();
        errors.Should().Equal(LauncherWatchdogTimeout.Tool);
    }

    internal sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        private TimerCallback? _callback;

        public Channel<Action> Timers { get; } = Channel.CreateUnbounded<Action>();

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            Timers.Writer.TryWrite(() => callback(state));
            return new ManualTimer();
        }

        public void Advance(TimeSpan amount) => _timestamp += amount.Ticks;

        public void Fire() => _callback?.Invoke(null);
    }

    private sealed class ManualTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
