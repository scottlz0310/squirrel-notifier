// <copyright file="ReviewCycleCoordinatorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class ReviewCycleCoordinatorTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ReviewCycleCoordinatorTests_{Guid.NewGuid():D}");
    private readonly LoggingService _loggingService;

    public ReviewCycleCoordinatorTests()
    {
        _loggingService = new LoggingService(_testDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ObserveEventAsync_ShouldStartAtRoundOneAndUpdatePresentation()
    {
        ReviewCycleCoordinator coordinator = CreateCoordinator();
        ReviewEvent reviewEvent = CreateReviewEvent("opened");
        List<ReviewCycleState> states = CaptureStates(coordinator, reviewEvent);

        await coordinator.ObserveEventAsync(reviewEvent);

        states.Should().ContainSingle();
        states[0].Round.Should().Be(1);
        states[0].Status.Should().Be(ReviewCycleStatus.AwaitingReviewer);
        reviewEvent.CycleRound.Should().Be(1);
        reviewEvent.CycleStatusLabel.Should().Be("ラウンド 1 — reviewer 起動待ち");
    }

    [Fact]
    public async Task ObserveEventAsync_ShouldIncrementOnlyForNewReReviewEvent()
    {
        ReviewCycleCoordinator coordinator = CreateCoordinator();
        ReviewEvent opened = CreateReviewEvent("opened", "event-opened");
        ReviewEvent synchronized = CreateReviewEvent("synchronized", "event-synchronized");
        ReviewEvent reReview = CreateReviewEvent("re-review-requested", "event-re-review");
        ReviewEvent duplicate = CreateReviewEvent("re-review-requested", "event-re-review");

        await coordinator.ObserveEventAsync(opened);
        await coordinator.ObserveEventAsync(synchronized);
        await coordinator.ObserveEventAsync(reReview);
        await coordinator.ObserveEventAsync(duplicate);

        ReviewCycleState state = (await new ReviewCycleStore(_testDirectory).TryGetAsync("owner/repo", 42))!;
        state.Round.Should().Be(2);
        state.ProcessedEventIds.Should().ContainInOrder("event-opened", "event-synchronized", "event-re-review");
    }

    [Fact]
    public async Task ObserveEventAsync_ShouldRestoreStateAcrossCoordinatorInstances()
    {
        ReviewCycleStore store = new(_testDirectory);
        ReviewEvent first = CreateReviewEvent("opened", "event-opened");
        await CreateCoordinator(store).ObserveEventAsync(first);

        ReviewEvent reReview = CreateReviewEvent("re-review-requested", "event-re-review");
        await CreateCoordinatorWithStore(store).ObserveEventAsync(reReview);

        ReviewCycleState state = (await store.TryGetAsync("owner/repo", 42))!;
        state.Round.Should().Be(2);
        state.Status.Should().Be(ReviewCycleStatus.AwaitingReviewer);
    }

    [Fact]
    public async Task ObserveEventAsync_ShouldDiscardExpiredMemoryState()
    {
        var timeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 9, 19, 0, 0, 0, TimeSpan.Zero));
        ReviewCycleStore store = new(_testDirectory, timeProvider);
        ReviewCycleCoordinator coordinator = CreateCoordinator(store, timeProvider);

        await coordinator.ObserveEventAsync(CreateReviewEvent("opened", "event-opened"));
        timeProvider.UtcNow += ReviewCycleStore.DefaultTtl;
        await coordinator.ObserveEventAsync(CreateReviewEvent("re-review-requested", "event-re-review"));

        ReviewCycleState state = (await store.TryGetAsync("owner/repo", 42))!;
        state.Round.Should().Be(1);
        state.ProcessedEventIds.Should().ContainSingle().Which.Should().Be("event-re-review");
    }

    [Theory]
    [InlineData(true, "ReviewerCompleted")]
    [InlineData(false, "ReviewerFailed")]
    public async Task MarkReviewerStartedAsync_ShouldReflectProcessCompletion(bool success, string expectedStatus)
    {
        ReviewCycleCoordinator coordinator = CreateCoordinator();
        ReviewEvent reviewEvent = CreateReviewEvent("opened");
        TaskCompletionSource<ReviewCycleState> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StateChanged += (_, args) =>
        {
            args.ReviewEvent.ApplyCycleState(args.State);
            if (args.State.Status is ReviewCycleStatus.ReviewerCompleted or ReviewCycleStatus.ReviewerFailed)
            {
                completed.TrySetResult(args.State);
            }
        };

        await coordinator.ObserveEventAsync(reviewEvent);
        AgentExecutionSession session = new(TimeProvider.System);
        await coordinator.MarkReviewerStartedAsync(
            reviewEvent,
            new ReviewStartLaunch(session, null!, null!, null!),
            "claude");

        reviewEvent.CycleStatus.Should().Be(ReviewCycleStatus.ReviewerRunning);
        (await new ReviewCycleStore(_testDirectory).TryGetAsync("owner/repo", 42))!.ActiveAgent.Should().Be("claude");
        session.Complete(
            success ? AgentExecutionOutcome.Succeeded : AgentExecutionOutcome.Failed,
            new LauncherResult { Success = success });

        ReviewCycleState state = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        state.Status.Should().Be(Enum.Parse<ReviewCycleStatus>(expectedStatus));
        state.ActiveAgent.Should().BeNull();
        reviewEvent.CycleStatusLabel.Should().Contain(success ? "結果未確認" : "失敗");
    }

    [Fact]
    public async Task MarkReviewerStartedAsync_ShouldCreateInitialStateWhenEventWasNotObserved()
    {
        ReviewCycleCoordinator coordinator = CreateCoordinator();
        ReviewEvent reviewEvent = CreateReviewEvent("opened");
        AgentExecutionSession session = new(TimeProvider.System);
        TaskCompletionSource<ReviewCycleState> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StateChanged += (_, args) =>
        {
            args.ReviewEvent.ApplyCycleState(args.State);
            if (args.State.Status == ReviewCycleStatus.ReviewerCompleted)
            {
                completed.TrySetResult(args.State);
            }
        };

        await coordinator.MarkReviewerStartedAsync(
            reviewEvent,
            new ReviewStartLaunch(session, null!, null!, null!));

        reviewEvent.CycleRound.Should().Be(1);
        reviewEvent.CycleStatus.Should().Be(ReviewCycleStatus.ReviewerRunning);
        session.Complete(
            AgentExecutionOutcome.Succeeded,
            new LauncherResult { Success = true });
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MarkReviewerStartedAsync_ShouldCompleteOldEventWhenNewEventArrivesDuringExecution()
    {
        ReviewCycleCoordinator coordinator = CreateCoordinator();
        ReviewEvent opened = CreateReviewEvent("opened", "event-opened");
        ReviewEvent reReview = CreateReviewEvent("re-review-requested", "event-re-review");
        TaskCompletionSource<ReviewCycleState> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StateChanged += (_, args) =>
        {
            args.ReviewEvent.ApplyCycleState(args.State);
            if (ReferenceEquals(args.ReviewEvent, opened)
                && args.State.Status == ReviewCycleStatus.ReviewerCompleted)
            {
                completed.TrySetResult(args.State);
            }
        };

        await coordinator.ObserveEventAsync(opened);
        AgentExecutionSession session = new(TimeProvider.System);
        await coordinator.MarkReviewerStartedAsync(
            opened,
            new ReviewStartLaunch(session, null!, null!, null!));

        await coordinator.ObserveEventAsync(reReview);
        reReview.CycleStatus.Should().Be(ReviewCycleStatus.ReviewerRunning);
        reReview.CycleRound.Should().Be(2);

        session.Complete(
            AgentExecutionOutcome.Succeeded,
            new LauncherResult { Success = true });

        ReviewCycleState completedState = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        completedState.Round.Should().Be(1);
        opened.CycleStatus.Should().Be(ReviewCycleStatus.ReviewerCompleted);
        reReview.CycleStatus.Should().Be(ReviewCycleStatus.AwaitingReviewer);

        ReviewCycleState persisted = (await new ReviewCycleStore(_testDirectory).TryGetAsync("owner/repo", 42))!;
        persisted.Round.Should().Be(2);
        persisted.Status.Should().Be(ReviewCycleStatus.AwaitingReviewer);
        persisted.ActiveEventId.Should().BeNull();
    }

    [Fact]
    public async Task ObserveEventAsync_ShouldIgnoreNonReviewerReason()
    {
        ReviewCycleCoordinator coordinator = CreateCoordinator();
        ReviewEvent reviewEvent = CreateReviewEvent("unknown");
        List<ReviewCycleState> states = CaptureStates(coordinator, reviewEvent);

        await coordinator.ObserveEventAsync(reviewEvent);

        states.Should().BeEmpty();
        reviewEvent.CycleRound.Should().Be(0);
    }

    // reviewer プロセスの終了コードは、終了を表す状態の通知にだけ付ける（公開状態 review-status.json の exitCode、#462）
    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(false, null)]
    public async Task MarkReviewerStartedAsync_ShouldPublishProcessExitCodeOnlyWithCompletion(bool success, int? exitCode)
    {
        ReviewCycleCoordinator coordinator = CreateCoordinator();
        ReviewEvent reviewEvent = CreateReviewEvent("opened");
        List<ReviewCycleStateChangedEventArgs> published = [];
        TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StateChanged += (_, args) =>
        {
            published.Add(args);
            if (args.State.Status is ReviewCycleStatus.ReviewerCompleted or ReviewCycleStatus.ReviewerFailed)
            {
                completed.TrySetResult();
            }
        };
        await coordinator.ObserveEventAsync(reviewEvent);
        AgentExecutionSession session = new(TimeProvider.System);
        await coordinator.MarkReviewerStartedAsync(
            reviewEvent,
            new ReviewStartLaunch(session, null!, null!, null!),
            "claude");

        session.Complete(
            success ? AgentExecutionOutcome.Succeeded : AgentExecutionOutcome.Failed,
            new LauncherResult { Success = success, ExitCode = exitCode });
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        published
            .Where(static args => args.State.Status is not (ReviewCycleStatus.ReviewerCompleted or ReviewCycleStatus.ReviewerFailed))
            .Should().OnlyContain(static args => args.ExitCode == null);
        published
            .Single(static args => args.State.Status is ReviewCycleStatus.ReviewerCompleted or ReviewCycleStatus.ReviewerFailed)
            .ExitCode.Should().Be(exitCode);
    }

    [Theory]
    [InlineData("Busy")]
    [InlineData("CiPending")]
    [InlineData("AutoPause")]
    public void ObserveHold_ShouldRaiseHoldObservedWithEventAndKind(string kind)
    {
        ReviewCycleCoordinator coordinator = CreateCoordinator();
        ReviewEvent reviewEvent = CreateReviewEvent("opened");
        List<ReviewHoldObservedEventArgs> observed = [];
        coordinator.HoldObserved += (_, args) => observed.Add(args);

        coordinator.ObserveHold(reviewEvent, Enum.Parse<ReviewHoldKind>(kind));

        ReviewHoldObservedEventArgs args = observed.Should().ContainSingle().Subject;
        args.ReviewEvent.Should().BeSameAs(reviewEvent);
        args.Kind.Should().Be(Enum.Parse<ReviewHoldKind>(kind));
    }

    [Fact]
    public async Task ObserveHold_ShouldLogAndNotThrow_WhenHandlerFails()
    {
        ReviewCycleCoordinator coordinator = CreateCoordinator();
        coordinator.HoldObserved += (_, _) => throw new InvalidOperationException("boom");

        Action observe = () => coordinator.ObserveHold(CreateReviewEvent("opened"), ReviewHoldKind.Busy);

        observe.Should().NotThrow();
        string logPath = Path.Combine(_testDirectory, "winui3.log");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!File.Exists(logPath) || !(await ReadLogAsync(logPath)).Contains("の保留の通知に失敗しました: boom", StringComparison.Ordinal))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }
    }

    private static async Task<string> ReadLogAsync(string path)
    {
        try
        {
            return await File.ReadAllTextAsync(path);
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private ReviewCycleCoordinator CreateCoordinator(
        IReviewCycleStore? store = null,
        TimeProvider? timeProvider = null)
        => CreateCoordinatorWithStore(store ?? new ReviewCycleStore(_testDirectory, timeProvider), timeProvider);

    private ReviewCycleCoordinator CreateCoordinatorWithStore(
        IReviewCycleStore store,
        TimeProvider? timeProvider = null)
        => new(store, _loggingService, timeProvider);

    private static List<ReviewCycleState> CaptureStates(
        ReviewCycleCoordinator coordinator,
        ReviewEvent reviewEvent)
    {
        List<ReviewCycleState> states = new();
        coordinator.StateChanged += (_, args) =>
        {
            args.ReviewEvent.ApplyCycleState(args.State);
            if (ReferenceEquals(args.ReviewEvent, reviewEvent))
            {
                states.Add(args.State);
            }
        };
        return states;
    }

    private static ReviewEvent CreateReviewEvent(string reason, string eventId = "event-opened")
        => new()
        {
            EventId = eventId,
            Repository = "owner/repo",
            PrNumber = 42,
            PrUrl = "https://github.com/owner/repo/pull/42",
            Reason = reason,
            Message = reason,
        };

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
