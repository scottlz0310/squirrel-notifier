// <copyright file="LauncherRecoveryPolicyTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class LauncherRecoveryPolicyTests
{
    [Fact]
    public void OnFailure_ShouldEscalateAfterSameFailureTwice()
    {
        var policy = new LauncherRecoveryPolicy();
        policy.OnFailure(LauncherWatchdogTimeout.NoProgress).Should().Be(LauncherRecoveryAction.Resume);
        policy.OnFailure(LauncherWatchdogTimeout.NoProgress).Should().Be(LauncherRecoveryAction.Escalate);
    }

    [Fact]
    public void OnFailure_ShouldLimitDifferentFailureRetriesToTwo()
    {
        var policy = new LauncherRecoveryPolicy();
        policy.OnFailure(LauncherWatchdogTimeout.NoProgress).Should().Be(LauncherRecoveryAction.Resume);
        policy.OnFailure(LauncherWatchdogTimeout.Tool).Should().Be(LauncherRecoveryAction.Resume);
        policy.OnFailure(LauncherWatchdogTimeout.Launcher).Should().Be(LauncherRecoveryAction.Escalate);
    }
}
