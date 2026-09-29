// <copyright file="ReviewEventProcessingCoordinatorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class ReviewEventProcessingCoordinatorTests : IDisposable
{
    private readonly string _logDirectory;
    private readonly LoggingService _loggingService;

    public ReviewEventProcessingCoordinatorTests()
    {
        _logDirectory = Path.Combine(Path.GetTempPath(), $"ReviewEventProcessingCoordinatorTests_{Guid.NewGuid()}");
        _loggingService = new LoggingService(_logDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_logDirectory))
        {
            Directory.Delete(_logDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessAsync_ShouldTrackEventAndStartReviewInOrder()
    {
        List<string> operations = new();
        var statusClient = new StubStatusClient(PullRequestLifecycleState.Open, operations);
        await using ReviewEventCleanupCoordinator cleanupCoordinator = CreateCleanupCoordinator(statusClient);
        ReviewEventCollectionCoordinator collectionCoordinator = new();
        List<string> startedEventIds = new();
        ReviewEventProcessingCoordinator coordinator = CreateCoordinator(
            collectionCoordinator,
            cleanupCoordinator,
            reviewEvent =>
            {
                operations.Add("start");
                startedEventIds.Add(reviewEvent.EventId);
                return Task.FromResult(ReviewStartResult.Skipped(ReviewStartStatus.SkippedDisabled));
            });

        ReviewEvent reviewEvent = CreateReviewEvent();

        ReviewEventProcessingResult result = await coordinator.ProcessAsync(reviewEvent);

        result.ShouldNotify.Should().BeTrue();
        result.StartResult!.Status.Should().Be(ReviewStartStatus.SkippedDisabled);
        collectionCoordinator.Events.Should().ContainSingle().Which.Should().Be(reviewEvent);
        cleanupCoordinator.TrackedEventCount.Should().Be(1);
        startedEventIds.Should().ContainSingle().Which.Should().Be(reviewEvent.EventId);
        statusClient.Calls.Should().ContainSingle().Which.Should().Be(("owner/repo", 42));
        operations.Should().Equal("status", "start");
    }

    [Fact]
    public async Task ProcessAsync_ShouldSkipClosedEventWithoutStartingReview()
    {
        var statusClient = new StubStatusClient(PullRequestLifecycleState.Merged);
        await using ReviewEventCleanupCoordinator cleanupCoordinator = CreateCleanupCoordinator(statusClient);
        ReviewEventCollectionCoordinator collectionCoordinator = new();
        cleanupCoordinator.EventsRemoved += (_, args) =>
        {
            foreach (string eventId in args.EventIds)
            {
                collectionCoordinator.RemoveByEventId(eventId);
            }
        };
        int startCalls = 0;
        ReviewEventProcessingCoordinator coordinator = CreateCoordinator(
            collectionCoordinator,
            cleanupCoordinator,
            _ =>
            {
                startCalls++;
                return Task.FromResult(ReviewStartResult.Skipped(ReviewStartStatus.SkippedDisabled));
            });

        ReviewEventProcessingResult result = await coordinator.ProcessAsync(CreateReviewEvent());

        result.ShouldNotify.Should().BeFalse();
        result.StartResult.Should().BeNull();
        startCalls.Should().Be(0);
        collectionCoordinator.Events.Should().BeEmpty();
        cleanupCoordinator.TrackedEventCount.Should().Be(0);
    }

    [Fact]
    public async Task ProcessAsync_WhenMaximumIsExceeded_ShouldUntrackEvictedEvent()
    {
        var statusClient = new StubStatusClient(PullRequestLifecycleState.Open);
        await using ReviewEventCleanupCoordinator cleanupCoordinator = CreateCleanupCoordinator(statusClient);
        ReviewEventCollectionCoordinator collectionCoordinator = new();
        ReviewEventProcessingCoordinator coordinator = CreateCoordinator(
            collectionCoordinator,
            cleanupCoordinator,
            _ => Task.FromResult(ReviewStartResult.Skipped(ReviewStartStatus.SkippedDisabled)));

        for (int index = 0; index <= ReviewEventCollectionCoordinator.MaxEvents; index++)
        {
            await coordinator.ProcessAsync(CreateReviewEvent($"event-{index}"));
        }

        collectionCoordinator.Events.Should().HaveCount(ReviewEventCollectionCoordinator.MaxEvents);
        cleanupCoordinator.TrackedEventCount.Should().Be(ReviewEventCollectionCoordinator.MaxEvents);
        collectionCoordinator.Events[^1].EventId.Should().Be("event-1");
    }

    [Fact]
    public async Task ProcessPendingAsync_ShouldStartFirstPendingEventAndKeepRest()
    {
        await using ReviewEventCleanupCoordinator cleanupCoordinator = CreateCleanupCoordinator(
            new StubStatusClient(PullRequestLifecycleState.Open));
        ReviewEventCollectionCoordinator collectionCoordinator = new();
        PendingReviewStartQueue pendingQueue = new();
        ReviewEvent first = CreateReviewEvent("evt_1", prNumber: 42);
        ReviewEvent second = CreateReviewEvent("evt_2", prNumber: 43);
        AddPending(collectionCoordinator, pendingQueue, first, second);
        List<string> startedEventIds = new();
        List<string> logLines = CaptureLogLines();
        ReviewEventProcessingCoordinator coordinator = CreateCoordinator(
            collectionCoordinator,
            cleanupCoordinator,
            reviewEvent =>
            {
                startedEventIds.Add(reviewEvent.EventId);
                return Task.FromResult(CreateStartedResult());
            },
            pendingQueue);

        PendingReviewStartResult? result = await coordinator.ProcessPendingAsync();

        result.Should().NotBeNull();
        result!.ReviewEvent.Should().BeSameAs(first);
        result.StartResult.IsStarted.Should().BeTrue();
        startedEventIds.Should().Equal("evt_1");
        pendingQueue.Snapshot()[0].Should().BeSameAs(second);
        logLines.Should().ContainSingle().Which.Should().Contain(
            "[Auto] 保留していた owner/repo #42 の自動起動を再評価します（reason: opened）。");
    }

    // 実行中（#339）・Auto-Pause 中（#340）はどちらも保留を残し、次の契機を待つ
    [Theory]
    [InlineData("SkippedBusy")]
    [InlineData("SkippedAutoPaused")]
    public async Task ProcessPendingAsync_ShouldKeepPendingEvent_WhenStillHeld(string status)
    {
        await using ReviewEventCleanupCoordinator cleanupCoordinator = CreateCleanupCoordinator(
            new StubStatusClient(PullRequestLifecycleState.Open));
        ReviewEventCollectionCoordinator collectionCoordinator = new();
        PendingReviewStartQueue pendingQueue = new();
        ReviewEvent first = CreateReviewEvent("evt_1", prNumber: 42);
        ReviewEvent second = CreateReviewEvent("evt_2", prNumber: 43);
        AddPending(collectionCoordinator, pendingQueue, first, second);
        int startCalls = 0;
        ReviewEventProcessingCoordinator coordinator = CreateCoordinator(
            collectionCoordinator,
            cleanupCoordinator,
            _ =>
            {
                startCalls++;
                return Task.FromResult(ReviewStartResult.Skipped(Enum.Parse<ReviewStartStatus>(status)));
            },
            pendingQueue);

        PendingReviewStartResult? result = await coordinator.ProcessPendingAsync();

        result.Should().BeNull();
        startCalls.Should().Be(1);
        pendingQueue.Count.Should().Be(2);
        pendingQueue.Snapshot()[0].Should().BeSameAs(first);
    }

    // 起動しなかった結果（設定 off・対象外・起動失敗）は保留から外し、次の保留を評価する
    [Theory]
    [InlineData("SkippedDisabled")]
    [InlineData("SkippedUnsupportedReason")]
    [InlineData("Failed")]
    public async Task ProcessPendingAsync_ShouldDropEventAndContinue_WhenNotStarted(string firstStatus)
    {
        await using ReviewEventCleanupCoordinator cleanupCoordinator = CreateCleanupCoordinator(
            new StubStatusClient(PullRequestLifecycleState.Open));
        ReviewEventCollectionCoordinator collectionCoordinator = new();
        PendingReviewStartQueue pendingQueue = new();
        ReviewEvent first = CreateReviewEvent("evt_1", prNumber: 42);
        ReviewEvent second = CreateReviewEvent("evt_2", prNumber: 43);
        AddPending(collectionCoordinator, pendingQueue, first, second);
        ReviewEventProcessingCoordinator coordinator = CreateCoordinator(
            collectionCoordinator,
            cleanupCoordinator,
            reviewEvent => Task.FromResult(ReferenceEquals(reviewEvent, first)
                ? CreateResult(Enum.Parse<ReviewStartStatus>(firstStatus))
                : CreateStartedResult()),
            pendingQueue);

        PendingReviewStartResult? result = await coordinator.ProcessPendingAsync();

        result!.ReviewEvent.Should().BeSameAs(second);
        pendingQueue.Count.Should().Be(0);
    }

    // CI の確定待ち（暫定、#456）は PR ごとの判定。保留を残したまま、後続の PR の評価を続ける
    [Fact]
    public async Task ProcessPendingAsync_ShouldKeepCiPendingEventAndContinueWithNext()
    {
        await using ReviewEventCleanupCoordinator cleanupCoordinator = CreateCleanupCoordinator(
            new StubStatusClient(PullRequestLifecycleState.Open));
        ReviewEventCollectionCoordinator collectionCoordinator = new();
        PendingReviewStartQueue pendingQueue = new();
        ReviewEvent first = CreateReviewEvent("evt_1", prNumber: 42);
        ReviewEvent second = CreateReviewEvent("evt_2", prNumber: 43);
        AddPending(collectionCoordinator, pendingQueue, first, second);
        List<string> evaluatedEventIds = new();
        ReviewEventProcessingCoordinator coordinator = CreateCoordinator(
            collectionCoordinator,
            cleanupCoordinator,
            reviewEvent =>
            {
                evaluatedEventIds.Add(reviewEvent.EventId);
                return Task.FromResult(ReferenceEquals(reviewEvent, first)
                    ? ReviewStartResult.Held(ReviewStartStatus.SkippedCiPending, "CI 完了待ち")
                    : CreateStartedResult());
            },
            pendingQueue);

        PendingReviewStartResult? result = await coordinator.ProcessPendingAsync();

        result!.ReviewEvent.Should().BeSameAs(second);
        evaluatedEventIds.Should().Equal("evt_1", "evt_2");
        pendingQueue.Snapshot().Should().Equal(first);
    }

    [Fact]
    public async Task ProcessPendingAsync_ShouldReturnNull_AndKeepAll_WhenEveryEventIsWaitingForCi()
    {
        await using ReviewEventCleanupCoordinator cleanupCoordinator = CreateCleanupCoordinator(
            new StubStatusClient(PullRequestLifecycleState.Open));
        ReviewEventCollectionCoordinator collectionCoordinator = new();
        PendingReviewStartQueue pendingQueue = new();
        ReviewEvent first = CreateReviewEvent("evt_1", prNumber: 42);
        ReviewEvent second = CreateReviewEvent("evt_2", prNumber: 43);
        AddPending(collectionCoordinator, pendingQueue, first, second);
        ReviewEventProcessingCoordinator coordinator = CreateCoordinator(
            collectionCoordinator,
            cleanupCoordinator,
            _ => Task.FromResult(ReviewStartResult.Held(ReviewStartStatus.SkippedCiPending, "CI 完了待ち")),
            pendingQueue);

        PendingReviewStartResult? result = await coordinator.ProcessPendingAsync();

        result.Should().BeNull();
        pendingQueue.Snapshot().Should().Equal(first, second);
    }

    // 確認の間隔（30 秒）ごとの再評価を毎回残すと Recent activity が埋まるため、CI の確定待ちは残さない
    [Fact]
    public async Task ProcessPendingAsync_ShouldNotLogReevaluation_ForEventWaitingForCi()
    {
        await using ReviewEventCleanupCoordinator cleanupCoordinator = CreateCleanupCoordinator(
            new StubStatusClient(PullRequestLifecycleState.Open));
        ReviewEventCollectionCoordinator collectionCoordinator = new();
        PendingReviewStartQueue pendingQueue = new();
        ReviewEvent waiting = CreateReviewEvent("evt_1", prNumber: 42);
        ReviewEvent other = CreateReviewEvent("evt_2", prNumber: 43);
        AddPending(collectionCoordinator, pendingQueue, waiting, other);
        List<string> logLines = CaptureLogLines();
        ReviewEventProcessingCoordinator coordinator = new(
            collectionCoordinator,
            cleanupCoordinator,
            pendingQueue,
            _loggingService,
            reviewEvent => Task.FromResult(ReferenceEquals(reviewEvent, waiting)
                ? ReviewStartResult.Held(ReviewStartStatus.SkippedCiPending, "CI 完了待ち")
                : CreateStartedResult()),
            reviewCycleCoordinator: null,
            isWaitingForCiSettle: reviewEvent => ReferenceEquals(reviewEvent, waiting));

        await coordinator.ProcessPendingAsync();

        logLines.Should().ContainSingle().Which.Should().Contain("保留していた owner/repo #43 の自動起動を再評価します");
    }

    // 未認証の GitHub API（60 req/h）を、30 秒ごとの再評価で使い切らない。PR の状態は判定の側が確認する
    [Fact]
    public async Task ProcessPendingAsync_ShouldNotCheckPullRequestStatus_ForEventWaitingForCi()
    {
        StubStatusClient statusClient = new(PullRequestLifecycleState.Open);
        await using ReviewEventCleanupCoordinator cleanupCoordinator = CreateCleanupCoordinator(statusClient);
        ReviewEventCollectionCoordinator collectionCoordinator = new();
        PendingReviewStartQueue pendingQueue = new();
        ReviewEvent waiting = CreateReviewEvent("evt_1", prNumber: 42);
        ReviewEvent other = CreateReviewEvent("evt_2", prNumber: 43);
        AddPending(collectionCoordinator, pendingQueue, waiting, other);
        ReviewEventProcessingCoordinator coordinator = new(
            collectionCoordinator,
            cleanupCoordinator,
            pendingQueue,
            _loggingService,
            reviewEvent => Task.FromResult(ReferenceEquals(reviewEvent, waiting)
                ? ReviewStartResult.Held(ReviewStartStatus.SkippedCiPending, "CI 完了待ち")
                : CreateStartedResult()),
            reviewCycleCoordinator: null,
            isWaitingForCiSettle: reviewEvent => ReferenceEquals(reviewEvent, waiting));

        await coordinator.ProcessPendingAsync();

        statusClient.Calls.Should().ContainSingle().Which.Should().Be(("owner/repo", 43));
    }

    // 判定の側が PR の close を見つけた場合は、保留から外す（起動しない）
    [Fact]
    public async Task ProcessPendingAsync_ShouldDropEvent_WhenPullRequestIsClosedWhileWaitingForCi()
    {
        await using ReviewEventCleanupCoordinator cleanupCoordinator = CreateCleanupCoordinator(
            new StubStatusClient(PullRequestLifecycleState.Open));
        ReviewEventCollectionCoordinator collectionCoordinator = new();
        PendingReviewStartQueue pendingQueue = new();
        ReviewEvent waiting = CreateReviewEvent("evt_1", prNumber: 42);
        AddPending(collectionCoordinator, pendingQueue, waiting);
        ReviewEventProcessingCoordinator coordinator = new(
            collectionCoordinator,
            cleanupCoordinator,
            pendingQueue,
            _loggingService,
            _ => Task.FromResult(ReviewStartResult.Skipped(ReviewStartStatus.SkippedPullRequestClosed)),
            reviewCycleCoordinator: null,
            isWaitingForCiSettle: _ => true);

        PendingReviewStartResult? result = await coordinator.ProcessPendingAsync();

        result.Should().BeNull();
        pendingQueue.Count.Should().Be(0);
    }

    // 評価の await 中に手動起動などで保留から外れたイベントを評価すると、再び保留へ戻してしまう
    [Fact]
    public async Task ProcessPendingAsync_ShouldSkipEventRemovedFromPendingDuringPass()
    {
        await using ReviewEventCleanupCoordinator cleanupCoordinator = CreateCleanupCoordinator(
            new StubStatusClient(PullRequestLifecycleState.Open));
        ReviewEventCollectionCoordinator collectionCoordinator = new();
        PendingReviewStartQueue pendingQueue = new();
        ReviewEvent first = CreateReviewEvent("evt_1", prNumber: 42);
        ReviewEvent second = CreateReviewEvent("evt_2", prNumber: 43);
        AddPending(collectionCoordinator, pendingQueue, first, second);
        List<string> evaluatedEventIds = new();
        ReviewEventProcessingCoordinator coordinator = CreateCoordinator(
            collectionCoordinator,
            cleanupCoordinator,
            reviewEvent =>
            {
                evaluatedEventIds.Add(reviewEvent.EventId);
                pendingQueue.RemovePullRequest(second);
                return Task.FromResult(ReviewStartResult.Held(ReviewStartStatus.SkippedCiPending, "CI 完了待ち"));
            },
            pendingQueue);

        await coordinator.ProcessPendingAsync();

        evaluatedEventIds.Should().Equal("evt_1");
        pendingQueue.Snapshot().Should().Equal(first);
    }

    [Fact]
    public async Task ProcessPendingAsync_ShouldDropEvent_WhenRemovedFromList()
    {
        await using ReviewEventCleanupCoordinator cleanupCoordinator = CreateCleanupCoordinator(
            new StubStatusClient(PullRequestLifecycleState.Open));
        ReviewEventCollectionCoordinator collectionCoordinator = new();
        PendingReviewStartQueue pendingQueue = new();
        ReviewEvent removed = CreateReviewEvent("evt_1", prNumber: 42);
        ReviewEvent kept = CreateReviewEvent("evt_2", prNumber: 43);
        AddPending(collectionCoordinator, pendingQueue, removed, kept);
        collectionCoordinator.Remove(removed);
        List<string> startedEventIds = new();
        List<string> logLines = CaptureLogLines();
        ReviewEventProcessingCoordinator coordinator = CreateCoordinator(
            collectionCoordinator,
            cleanupCoordinator,
            reviewEvent =>
            {
                startedEventIds.Add(reviewEvent.EventId);
                return Task.FromResult(CreateStartedResult());
            },
            pendingQueue);

        PendingReviewStartResult? result = await coordinator.ProcessPendingAsync();

        result!.ReviewEvent.Should().BeSameAs(kept);
        startedEventIds.Should().Equal("evt_2");
        logLines.Should().Contain(line => line.Contains(
            "[Auto] 保留していた owner/repo #42 は一覧から削除されたため、自動起動しません。",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessPendingAsync_ShouldDropEventWithoutStarting_WhenPullRequestIsClosed()
    {
        var statusClient = new StubStatusClient(PullRequestLifecycleState.Merged);
        await using ReviewEventCleanupCoordinator cleanupCoordinator = CreateCleanupCoordinator(statusClient);
        ReviewEventCollectionCoordinator collectionCoordinator = new();
        PendingReviewStartQueue pendingQueue = new();
        AddPending(collectionCoordinator, pendingQueue, CreateReviewEvent());
        int startCalls = 0;
        ReviewEventProcessingCoordinator coordinator = CreateCoordinator(
            collectionCoordinator,
            cleanupCoordinator,
            _ =>
            {
                startCalls++;
                return Task.FromResult(CreateStartedResult());
            },
            pendingQueue);

        PendingReviewStartResult? result = await coordinator.ProcessPendingAsync();

        result.Should().BeNull();
        startCalls.Should().Be(0);
        pendingQueue.Count.Should().Be(0);
        statusClient.Calls.Should().ContainSingle();
    }

    // 実行終了と起動放棄の両方が再評価を促すため、再評価の await 中に再び呼ばれ得る。
    // 重ねて評価すると同じイベントを二重に起動し得るので、進行中の再評価の後に評価し直す（#339）
    [Fact]
    public async Task ProcessPendingAsync_ShouldReevaluateAfterCurrentRun_WhenCalledDuringReevaluation()
    {
        await using ReviewEventCleanupCoordinator cleanupCoordinator = CreateCleanupCoordinator(
            new StubStatusClient(PullRequestLifecycleState.Open));
        ReviewEventCollectionCoordinator collectionCoordinator = new();
        PendingReviewStartQueue pendingQueue = new();
        ReviewEvent pendingEvent = CreateReviewEvent();
        AddPending(collectionCoordinator, pendingQueue, pendingEvent);
        TaskCompletionSource firstStartEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ReviewStartResult> firstStartResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int startCalls = 0;
        ReviewEventProcessingCoordinator coordinator = CreateCoordinator(
            collectionCoordinator,
            cleanupCoordinator,
            _ =>
            {
                startCalls++;
                if (startCalls == 1)
                {
                    firstStartEntered.SetResult();
                    return firstStartResult.Task;
                }

                return Task.FromResult(CreateStartedResult());
            },
            pendingQueue);

        Task<PendingReviewStartResult?> current = coordinator.ProcessPendingAsync();
        await firstStartEntered.Task;
        PendingReviewStartResult? overlapping = await coordinator.ProcessPendingAsync();
        firstStartResult.SetResult(ReviewStartResult.Skipped(ReviewStartStatus.SkippedBusy));
        PendingReviewStartResult? result = await current;

        overlapping.Should().BeNull();
        startCalls.Should().Be(2);
        result!.ReviewEvent.Should().BeSameAs(pendingEvent);
        pendingQueue.Count.Should().Be(0);
    }

    [Fact]
    public async Task ProcessPendingAsync_ShouldReturnNull_WhenNothingIsPending()
    {
        await using ReviewEventCleanupCoordinator cleanupCoordinator = CreateCleanupCoordinator(
            new StubStatusClient(PullRequestLifecycleState.Open));
        int startCalls = 0;
        ReviewEventProcessingCoordinator coordinator = CreateCoordinator(
            new ReviewEventCollectionCoordinator(),
            cleanupCoordinator,
            _ =>
            {
                startCalls++;
                return Task.FromResult(CreateStartedResult());
            });

        PendingReviewStartResult? result = await coordinator.ProcessPendingAsync();

        result.Should().BeNull();
        startCalls.Should().Be(0);
    }

    [Fact]
    public async Task ProcessAsync_ShouldRejectNullEvent()
    {
        await using ReviewEventCleanupCoordinator cleanupCoordinator = CreateCleanupCoordinator(
            new StubStatusClient(PullRequestLifecycleState.Open));
        ReviewEventProcessingCoordinator coordinator = CreateCoordinator(
            new ReviewEventCollectionCoordinator(),
            cleanupCoordinator,
            _ => Task.FromResult(ReviewStartResult.Skipped(ReviewStartStatus.SkippedDisabled)));

        Func<Task> act = () => coordinator.ProcessAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Constructor_ShouldRejectNullDependencies()
    {
        ReviewEventCollectionCoordinator collectionCoordinator = new();
        ReviewEventCleanupCoordinator cleanupCoordinator = CreateCleanupCoordinator(
            new StubStatusClient(PullRequestLifecycleState.Open));
        Func<ReviewEvent, Task<ReviewStartResult>> starter = _ =>
            Task.FromResult(ReviewStartResult.Skipped(ReviewStartStatus.SkippedDisabled));

        PendingReviewStartQueue pendingQueue = new();

        Action nullCollection = () => new ReviewEventProcessingCoordinator(null!, cleanupCoordinator, pendingQueue, _loggingService, starter);
        Action nullCleanup = () => new ReviewEventProcessingCoordinator(collectionCoordinator, null!, pendingQueue, _loggingService, starter);
        Action nullPendingQueue = () => new ReviewEventProcessingCoordinator(collectionCoordinator, cleanupCoordinator, null!, _loggingService, starter);
        Action nullLogging = () => new ReviewEventProcessingCoordinator(collectionCoordinator, cleanupCoordinator, pendingQueue, null!, starter);
        Action nullStarter = () => new ReviewEventProcessingCoordinator(collectionCoordinator, cleanupCoordinator, pendingQueue, _loggingService, null!);

        nullCollection.Should().Throw<ArgumentNullException>();
        nullCleanup.Should().Throw<ArgumentNullException>();
        nullPendingQueue.Should().Throw<ArgumentNullException>();
        nullLogging.Should().Throw<ArgumentNullException>();
        nullStarter.Should().Throw<ArgumentNullException>();

        await cleanupCoordinator.DisposeAsync();
    }

    private ReviewEventCleanupCoordinator CreateCleanupCoordinator(IPullRequestStatusClient statusClient)
        => new(statusClient, _loggingService);

    private static void AddPending(
        ReviewEventCollectionCoordinator collectionCoordinator,
        PendingReviewStartQueue pendingQueue,
        params ReviewEvent[] reviewEvents)
    {
        foreach (ReviewEvent reviewEvent in reviewEvents)
        {
            collectionCoordinator.Add(reviewEvent);
            pendingQueue.AddOrReplace(reviewEvent);
        }
    }

    private static ReviewStartResult CreateStartedResult()
        => ReviewStartResult.Launched(new ReviewStartLaunch(null!, null!, null!, null!));

    private static ReviewStartResult CreateResult(ReviewStartStatus status)
        => status == ReviewStartStatus.Failed ? ReviewStartResult.Failure("起動できません") : ReviewStartResult.Skipped(status);

    private List<string> CaptureLogLines()
    {
        List<string> logLines = new();
        _loggingService.LogAppended += (_, line) => logLines.Add(line);
        return logLines;
    }

    private ReviewEventProcessingCoordinator CreateCoordinator(
        ReviewEventCollectionCoordinator collectionCoordinator,
        ReviewEventCleanupCoordinator cleanupCoordinator,
        Func<ReviewEvent, Task<ReviewStartResult>> starter,
        PendingReviewStartQueue? pendingQueue = null)
        => new(collectionCoordinator, cleanupCoordinator, pendingQueue ?? new PendingReviewStartQueue(), _loggingService, starter);

    private static ReviewEvent CreateReviewEvent(string eventId = "evt_1", int prNumber = 42)
        => new()
        {
            EventId = eventId,
            Repository = "owner/repo",
            PrNumber = prNumber,
            PrUrl = $"https://github.com/owner/repo/pull/{prNumber}",
            Reason = "opened",
            Message = "Review requested",
        };

    private sealed class StubStatusClient : IPullRequestStatusClient
    {
        private readonly PullRequestLifecycleState _state;
        private readonly List<string>? _operations;

        public StubStatusClient(PullRequestLifecycleState state, List<string>? operations = null)
        {
            _state = state;
            _operations = operations;
        }

        public List<(string Repository, int PrNumber)> Calls { get; } = new();

        public Task<PullRequestLifecycleState> GetStateAsync(
            string repository,
            int prNumber,
            CancellationToken cancellationToken)
        {
            _operations?.Add("status");
            Calls.Add((repository, prNumber));
            return Task.FromResult(_state);
        }
    }
}
