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
}
