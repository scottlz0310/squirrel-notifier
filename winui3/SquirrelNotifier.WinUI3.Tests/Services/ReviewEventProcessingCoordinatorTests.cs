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

        Action nullCollection = () => new ReviewEventProcessingCoordinator(null!, cleanupCoordinator, starter);
        Action nullCleanup = () => new ReviewEventProcessingCoordinator(collectionCoordinator, null!, starter);
        Action nullStarter = () => new ReviewEventProcessingCoordinator(collectionCoordinator, cleanupCoordinator, null!);

        nullCollection.Should().Throw<ArgumentNullException>();
        nullCleanup.Should().Throw<ArgumentNullException>();
        nullStarter.Should().Throw<ArgumentNullException>();

        await cleanupCoordinator.DisposeAsync();
    }

    private ReviewEventCleanupCoordinator CreateCleanupCoordinator(IPullRequestStatusClient statusClient)
        => new(statusClient, _loggingService);

    private static ReviewEventProcessingCoordinator CreateCoordinator(
        ReviewEventCollectionCoordinator collectionCoordinator,
        ReviewEventCleanupCoordinator cleanupCoordinator,
        Func<ReviewEvent, Task<ReviewStartResult>> starter)
        => new(collectionCoordinator, cleanupCoordinator, starter);

    private static ReviewEvent CreateReviewEvent(string eventId = "evt_1")
        => new()
        {
            EventId = eventId,
            Repository = "owner/repo",
            PrNumber = 42,
            PrUrl = "https://github.com/owner/repo/pull/42",
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
