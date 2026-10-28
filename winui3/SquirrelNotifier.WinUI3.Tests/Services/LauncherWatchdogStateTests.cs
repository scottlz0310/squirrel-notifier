// <copyright file="LauncherWatchdogStateTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class LauncherWatchdogStateTests
{
    [Fact]
    public void Check_ShouldPauseBothBudgetsWhileAnyToolIsActive()
    {
        var state = new LauncherWatchdogState(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30));
        state.Observe(TimeSpan.FromMinutes(9), [new(LauncherToolActivityKind.Started, "a")]);
        state.Observe(TimeSpan.FromMinutes(10), [new(LauncherToolActivityKind.Started, "b")]);
        state.Observe(TimeSpan.FromMinutes(20), [new(LauncherToolActivityKind.Completed, "a")]);
        state.Check(TimeSpan.FromMinutes(35)).Should().Be(LauncherWatchdogTimeout.None);
        state.Observe(TimeSpan.FromMinutes(36), [new(LauncherToolActivityKind.Completed, "b")]);
        state.Check(TimeSpan.FromMinutes(45)).Should().Be(LauncherWatchdogTimeout.None);
        state.Check(TimeSpan.FromMinutes(46)).Should().Be(LauncherWatchdogTimeout.NoProgress);
    }

    [Fact]
    public void Check_ShouldEnforceLimitPerToolEvenWhenOtherToolsFinish()
    {
        var state = new LauncherWatchdogState(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30));
        state.Observe(TimeSpan.FromMinutes(1), [new(LauncherToolActivityKind.Started, "a"), new(LauncherToolActivityKind.Started, "b")]);
        state.Observe(TimeSpan.FromMinutes(20), [new(LauncherToolActivityKind.Completed, "b")]);
        state.Check(TimeSpan.FromMinutes(31)).Should().Be(LauncherWatchdogTimeout.Tool);
    }

    [Fact]
    public void Check_ShouldResumeLauncherBudgetAfterToolCompletion()
    {
        var state = new LauncherWatchdogState(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(40), TimeSpan.FromMinutes(30));
        state.Observe(TimeSpan.FromMinutes(20), [new(LauncherToolActivityKind.Started, "a")]);
        state.Observe(TimeSpan.FromMinutes(45), [new(LauncherToolActivityKind.Completed, "a")]);
        state.Check(TimeSpan.FromMinutes(54)).Should().Be(LauncherWatchdogTimeout.None);
        state.Check(TimeSpan.FromMinutes(55)).Should().Be(LauncherWatchdogTimeout.Launcher);
    }

    [Fact]
    public void Observe_ShouldNotResetAnExpiredDeadline()
    {
        var state = new LauncherWatchdogState(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30));
        state.Observe(TimeSpan.FromMinutes(10), [new(LauncherToolActivityKind.Started, "a")]).Should().Be(LauncherWatchdogTimeout.NoProgress);
    }

    [Fact]
    public void Observe_ShouldNotExtendDuplicateToolStart()
    {
        var state = new LauncherWatchdogState(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30));
        state.Observe(TimeSpan.FromMinutes(1), [new(LauncherToolActivityKind.Started, "a")]);
        state.Observe(TimeSpan.FromMinutes(20), [new(LauncherToolActivityKind.Started, "a")]);
        state.Check(TimeSpan.FromMinutes(31)).Should().Be(LauncherWatchdogTimeout.Tool);
    }
}
