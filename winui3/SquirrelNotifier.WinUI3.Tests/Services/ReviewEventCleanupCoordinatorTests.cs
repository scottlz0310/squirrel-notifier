// <copyright file="ReviewEventCleanupCoordinatorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class ReviewEventCleanupCoordinatorTests : IDisposable
{
    private readonly string _logDirectory;
    private readonly LoggingService _loggingService;

    public ReviewEventCleanupCoordinatorTests()
    {
        _logDirectory = Path.Combine(Path.GetTempPath(), $"ReviewEventCleanupCoordinatorTests_{Guid.NewGuid()}");
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
    public async Task IsActionAllowedAsync_ShouldRemoveMergedEvent()
    {
        var statusClient = new StubStatusClient(PullRequestLifecycleState.Merged);
        await using ReviewEventCleanupCoordinator coordinator = CreateCoordinator(statusClient);
        ReviewEvent reviewEvent = CreateReviewEvent();
        List<string> removedIds = new();
        coordinator.EventsRemoved += (_, args) => removedIds.AddRange(args.EventIds);

        bool allowed = await coordinator.IsActionAllowedAsync(reviewEvent, CancellationToken.None);

        allowed.Should().BeFalse();
        coordinator.TrackedEventCount.Should().Be(0);
        removedIds.Should().ContainSingle().Which.Should().Be(reviewEvent.EventId);
        statusClient.Calls.Should().ContainSingle().Which.Should().Be(("owner/repo", 42));
    }

    [Fact]
    public async Task IsActionAllowedAsync_ShouldKeepOpenEvent()
    {
        var statusClient = new StubStatusClient(PullRequestLifecycleState.Open);
        await using ReviewEventCleanupCoordinator coordinator = CreateCoordinator(statusClient);

        bool allowed = await coordinator.IsActionAllowedAsync(CreateReviewEvent(), CancellationToken.None);

        allowed.Should().BeTrue();
        coordinator.TrackedEventCount.Should().Be(1);
    }

    [Fact]
    public async Task RefreshAsync_ShouldRemoveClosedEventAndKeepOpenEvent()
    {
        var statusClient = new SequenceStatusClient(
            PullRequestLifecycleState.Closed,
            PullRequestLifecycleState.Open);
        await using ReviewEventCleanupCoordinator coordinator = CreateCoordinator(statusClient);
        ReviewEvent closedEvent = CreateReviewEvent("closed");
        ReviewEvent openEvent = CreateReviewEvent("open", 43);
        List<string> removedIds = new();
        coordinator.EventsRemoved += (_, args) => removedIds.AddRange(args.EventIds);
        coordinator.Track(closedEvent);
        coordinator.Track(openEvent);

        await coordinator.RefreshAsync();

        coordinator.TrackedEventCount.Should().Be(1);
        removedIds.Should().ContainSingle().Which.Should().Be(closedEvent.EventId);
    }

    [Fact]
    public async Task IsActionAllowedAsync_ShouldKeepEventAndLog_WhenStatusLookupFails()
    {
        var statusClient = new StubStatusClient(new HttpRequestException("rate limited"));
        await using ReviewEventCleanupCoordinator coordinator = CreateCoordinator(statusClient);
        ReviewEvent reviewEvent = CreateReviewEvent();

        bool allowed = await coordinator.IsActionAllowedAsync(reviewEvent, CancellationToken.None);

        allowed.Should().BeTrue();
        coordinator.TrackedEventCount.Should().Be(1);
        string log = await File.ReadAllTextAsync(Path.Combine(_logDirectory, "winui3.log"));
        log.Should().Contain("イベントは保持します").And.Contain("rate limited");
    }

    [Fact]
    public async Task RefreshAsync_ShouldKeepEventAndLog_WhenStatusLookupFails()
    {
        var statusClient = new StubStatusClient(new InvalidOperationException("temporary failure"));
        await using ReviewEventCleanupCoordinator coordinator = CreateCoordinator(statusClient);
        coordinator.Track(CreateReviewEvent());

        await coordinator.RefreshAsync();

        coordinator.TrackedEventCount.Should().Be(1);
        string log = await File.ReadAllTextAsync(Path.Combine(_logDirectory, "winui3.log"));
        log.Should().Contain("イベントは保持します").And.Contain("temporary failure");
    }

    [Fact]
    public async Task Untrack_ShouldPreventLaterAutomaticRemoval()
    {
        var statusClient = new StubStatusClient(PullRequestLifecycleState.Merged);
        await using ReviewEventCleanupCoordinator coordinator = CreateCoordinator(statusClient);
        ReviewEvent reviewEvent = CreateReviewEvent();
        coordinator.Track(reviewEvent);
        coordinator.Untrack(reviewEvent.EventId);

        await coordinator.RefreshAsync();

        coordinator.TrackedEventCount.Should().Be(0);
        statusClient.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Untrack_ShouldIgnoreBlankEventId()
    {
        var statusClient = new StubStatusClient(PullRequestLifecycleState.Open);
        await using ReviewEventCleanupCoordinator coordinator = CreateCoordinator(statusClient);

        coordinator.Untrack(" ");

        coordinator.TrackedEventCount.Should().Be(0);
    }

    [Fact]
    public void Constructor_ShouldRejectNullDependenciesAndInvalidInterval()
    {
        Action nullStatusClient = () => new ReviewEventCleanupCoordinator(null!, _loggingService);
        Action nullLoggingService = () => new ReviewEventCleanupCoordinator(new StubStatusClient(PullRequestLifecycleState.Open), null!);
        Action invalidInterval = () => new ReviewEventCleanupCoordinator(
            new StubStatusClient(PullRequestLifecycleState.Open),
            _loggingService,
            TimeSpan.Zero);

        nullStatusClient.Should().Throw<ArgumentNullException>();
        nullLoggingService.Should().Throw<ArgumentNullException>();
        invalidInterval.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task IsActionAllowedAsync_ShouldRethrowCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var statusClient = new CancellationStatusClient(cancellation);
        await using ReviewEventCleanupCoordinator coordinator = CreateCoordinator(statusClient);

        Func<Task> act = () => coordinator.IsActionAllowedAsync(CreateReviewEvent(), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RefreshAsync_ShouldStopWhenStatusLookupIsCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        var statusClient = new CancellationStatusClient(cancellation);
        await using ReviewEventCleanupCoordinator coordinator = CreateCoordinator(statusClient);
        coordinator.Track(CreateReviewEvent());

        await coordinator.RefreshAsync(cancellation.Token);

        coordinator.TrackedEventCount.Should().Be(1);
    }

    [Fact]
    public async Task RefreshAsync_ShouldIgnoreConcurrentRefresh()
    {
        var statusClient = new BlockingStatusClient();
        await using ReviewEventCleanupCoordinator coordinator = CreateCoordinator(statusClient);
        coordinator.Track(CreateReviewEvent());

        Task firstRefresh = coordinator.RefreshAsync();
        await statusClient.Entered.Task;
        await coordinator.RefreshAsync();
        statusClient.Calls.Should().Be(1);

        statusClient.Release.TrySetResult(true);
        await firstRefresh;
    }

    [Fact]
    public async Task IsActionAllowedAsync_ShouldLogWhenRemovalNotificationFails()
    {
        var statusClient = new StubStatusClient(PullRequestLifecycleState.Merged);
        await using ReviewEventCleanupCoordinator coordinator = CreateCoordinator(statusClient);
        coordinator.EventsRemoved += (_, _) => throw new InvalidOperationException("handler failed");

        bool allowed = await coordinator.IsActionAllowedAsync(CreateReviewEvent(), CancellationToken.None);
        await Task.Delay(50);

        allowed.Should().BeFalse();
        string log = await File.ReadAllTextAsync(Path.Combine(_logDirectory, "winui3.log"));
        log.Should().Contain("自動削除通知に失敗しました").And.Contain("handler failed");
    }

    [Fact]
    public async Task StartAndDisposeAsync_ShouldStopPollingWithoutThrowing()
    {
        var statusClient = new StubStatusClient(PullRequestLifecycleState.Open);
        await using ReviewEventCleanupCoordinator coordinator = CreateCoordinator(statusClient, TimeSpan.FromMilliseconds(10));
        coordinator.Start();
        coordinator.Track(CreateReviewEvent());

        await Task.Delay(50);
    }

    private ReviewEventCleanupCoordinator CreateCoordinator(
        IPullRequestStatusClient statusClient,
        TimeSpan? pollInterval = null)
        => new(statusClient, _loggingService, pollInterval);

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
        private readonly PullRequestLifecycleState? _state;
        private readonly Exception? _exception;

        public StubStatusClient(PullRequestLifecycleState state)
        {
            _state = state;
        }

        public StubStatusClient(Exception exception)
        {
            _exception = exception;
        }

        public List<(string Repository, int PrNumber)> Calls { get; } = new();

        public Task<PullRequestLifecycleState> GetStateAsync(string repository, int prNumber, CancellationToken cancellationToken)
        {
            Calls.Add((repository, prNumber));
            if (_exception != null)
            {
                throw _exception;
            }

            return Task.FromResult(_state!.Value);
        }
    }

    private sealed class SequenceStatusClient : IPullRequestStatusClient
    {
        private readonly Queue<PullRequestLifecycleState> _states;

        public SequenceStatusClient(params PullRequestLifecycleState[] states)
        {
            _states = new Queue<PullRequestLifecycleState>(states);
        }

        public Task<PullRequestLifecycleState> GetStateAsync(string repository, int prNumber, CancellationToken cancellationToken)
            => Task.FromResult(_states.Dequeue());
    }

    private sealed class CancellationStatusClient : IPullRequestStatusClient
    {
        private readonly CancellationTokenSource _cancellation;

        public CancellationStatusClient(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public Task<PullRequestLifecycleState> GetStateAsync(string repository, int prNumber, CancellationToken cancellationToken)
        {
            _cancellation.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class BlockingStatusClient : IPullRequestStatusClient
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls { get; private set; }

        public async Task<PullRequestLifecycleState> GetStateAsync(string repository, int prNumber, CancellationToken cancellationToken)
        {
            Calls++;
            Entered.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return PullRequestLifecycleState.Open;
        }
    }
}
