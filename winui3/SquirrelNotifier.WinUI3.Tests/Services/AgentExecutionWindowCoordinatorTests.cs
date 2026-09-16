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

    // 前の実行のウィンドウが残っている間に起動したセッションは捨てず、閉じた順に開く（#339）
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Show_ShouldOpenDeferredWindowsInOrder_WhenCurrentWindowCloses(int deferredCount)
    {
        List<FakeAgentExecutionWindow> windows = new();
        List<ReviewStartLaunch> openedLaunches = new();
        AgentExecutionWindowCoordinator coordinator = new(launch =>
        {
            openedLaunches.Add(launch);
            FakeAgentExecutionWindow window = new();
            windows.Add(window);
            return window;
        });
        List<ReviewStartResult> results = Enumerable.Range(0, deferredCount + 1)
            .Select(_ => ReviewStartResult.Launched(new ReviewStartLaunch(new AgentExecutionSession(TimeProvider.System), null!, null!, null!)))
            .ToList();

        foreach (ReviewStartResult result in results)
        {
            coordinator.Show(result);
        }

        windows.Should().ContainSingle();
        coordinator.DeferredCount.Should().Be(deferredCount);

        for (int index = 0; index < deferredCount; index++)
        {
            windows[index].RaiseClosed();
        }

        openedLaunches.Should().Equal(results.Select(result => result.Launch!));
        windows.Should().OnlyContain(window => window.ActivateCount == 1);
        coordinator.DeferredCount.Should().Be(0);
        coordinator.HasActiveWindow.Should().BeTrue();
    }

    [Fact]
    public void Show_ShouldIgnoreClosedEventFromInactiveWindow()
    {
        FakeAgentExecutionWindow firstWindow = new();
        FakeAgentExecutionWindow secondWindow = new();
        Queue<IAgentExecutionWindow> windows = new([firstWindow, secondWindow]);
        AgentExecutionWindowCoordinator coordinator = new(_ => windows.Dequeue());

        coordinator.Show(CreateStartedResult());
        coordinator.Show(CreateStartedResult());
        firstWindow.RaiseClosed();
        firstWindow.RaiseClosed();

        secondWindow.ActivateCount.Should().Be(1);
        coordinator.HasActiveWindow.Should().BeTrue();
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
