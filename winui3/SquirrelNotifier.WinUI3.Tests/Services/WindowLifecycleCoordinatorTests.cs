// <copyright file="WindowLifecycleCoordinatorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Collections.Generic;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class WindowLifecycleCoordinatorTests
{
    [Fact]
    public void RequestExit_ShouldRunCleanupBeforeClosing()
    {
        List<string> actions = [];
        WindowLifecycleCoordinator coordinator = CreateCoordinator(actions);

        coordinator.IsExitRequested.Should().BeFalse();

        coordinator.RequestExit();

        coordinator.IsExitRequested.Should().BeTrue();
        actions.Should().Equal("unsubscribe", "dispose", "close");
    }

    [Fact]
    public void RequestExit_ShouldIgnoreSubsequentRequests()
    {
        List<string> actions = [];
        WindowLifecycleCoordinator coordinator = CreateCoordinator(actions);

        coordinator.RequestExit();
        coordinator.RequestExit();

        actions.Should().Equal("unsubscribe", "dispose", "close");
    }

    private static WindowLifecycleCoordinator CreateCoordinator(List<string> actions)
    {
        return new WindowLifecycleCoordinator(
            () => actions.Add("unsubscribe"),
            () => actions.Add("dispose"),
            () => actions.Add("close"));
    }
}
