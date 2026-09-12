// <copyright file="AgentExecutionWindowCoordinatorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class AgentExecutionWindowCoordinatorTests
{
    [Fact]
    public void Show_ShouldIgnoreResultWithoutLaunch()
    {
        int createCount = 0;
        AgentExecutionWindowCoordinator coordinator = new(_ =>
        {
            createCount++;
            return new FakeAgentExecutionWindow();
        });

        coordinator.Show(ReviewStartResult.Skipped(ReviewStartStatus.SkippedBusy));

        createCount.Should().Be(0);
        coordinator.HasActiveWindow.Should().BeFalse();
    }

    [Fact]
    public void Show_ShouldKeepOneActiveWindow_WhenCalledAgain()
    {
        FakeAgentExecutionWindow window = new();
        int createCount = 0;
        AgentExecutionWindowCoordinator coordinator = new(_ =>
        {
            createCount++;
            return window;
        });

        coordinator.Show(CreateStartedResult());
        coordinator.Show(CreateStartedResult());

        createCount.Should().Be(1);
        window.ActivateCount.Should().Be(1);
        coordinator.HasActiveWindow.Should().BeTrue();
    }

    [Fact]
    public void Show_ShouldAllowNextWindowAfterCurrentWindowCloses()
    {
        FakeAgentExecutionWindow firstWindow = new();
        FakeAgentExecutionWindow secondWindow = new();
        Queue<IAgentExecutionWindow> windows = new([firstWindow, secondWindow]);
        AgentExecutionWindowCoordinator coordinator = new(_ => windows.Dequeue());

        coordinator.Show(CreateStartedResult());
        firstWindow.RaiseClosed();
        coordinator.Show(CreateStartedResult());

        coordinator.HasActiveWindow.Should().BeTrue();
        firstWindow.ActivateCount.Should().Be(1);
        secondWindow.ActivateCount.Should().Be(1);
    }

    private static ReviewStartResult CreateStartedResult()
        => ReviewStartResult.Launched(new ReviewStartLaunch(null!, null!, null!, null!));

    private sealed class FakeAgentExecutionWindow : IAgentExecutionWindow
    {
        public event EventHandler? Closed;

        public int ActivateCount { get; private set; }

        public void Activate() => ActivateCount++;

        public void RaiseClosed() => Closed?.Invoke(this, EventArgs.Empty);
    }
}
