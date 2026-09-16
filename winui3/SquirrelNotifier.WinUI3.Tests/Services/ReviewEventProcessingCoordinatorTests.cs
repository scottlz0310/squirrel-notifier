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
        pendingQueue.Peek().Should().BeSameAs(second);
        logLines.Should().ContainSingle().Which.Should().Contain(
            "[Auto] 保留していた owner/repo #42 の自動起動を再評価します（reason: opened）。");
    }

    [Fact]
    public async Task ProcessPendingAsync_ShouldKeepPendingEvent_WhenStillBusy()
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
                return Task.FromResult(ReviewStartResult.Skipped(ReviewStartStatus.SkippedBusy));
            },
            pendingQueue);

        PendingReviewStartResult? result = await coordinator.ProcessPendingAsync();

        result.Should().BeNull();
        startCalls.Should().Be(1);
        pendingQueue.Count.Should().Be(2);
        pendingQueue.Peek().Should().BeSameAs(first);
    }

    // 起動しなかった結果（設定 off・Auto-Pause・対象外・起動失敗）は保留から外し、次の保留を評価する
    [Theory]
    [InlineData("SkippedDisabled")]
    [InlineData("SkippedAutoPaused")]
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
