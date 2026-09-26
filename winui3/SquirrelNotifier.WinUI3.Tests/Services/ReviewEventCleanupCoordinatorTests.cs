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

    // reviewer の作業領域の片付け（#403）は、巡回でマージ済み・クローズ済みと判明した PR ごとに 1 回通知を受ける
    [Theory]
    [InlineData("Merged", true)]
    [InlineData("Closed", true)]
    [InlineData("Open", false)]
    public async Task RefreshAsync_ShouldRaisePullRequestClosed_OnlyForCompletedPullRequest(
        string stateName,
        bool expectedRaised)
    {
        PullRequestLifecycleState state = Enum.Parse<PullRequestLifecycleState>(stateName);
        await using ReviewEventCleanupCoordinator coordinator = CreateCoordinator(new StubStatusClient(state));
        List<(string Repository, int PrNumber)> closed = new();
        coordinator.PullRequestClosed += (_, args) => closed.Add((args.Repository, args.PrNumber));
        coordinator.Track(CreateReviewEvent("first"));
        coordinator.Track(CreateReviewEvent("second"));

        await coordinator.RefreshAsync();
        await coordinator.RefreshAsync();

        if (expectedRaised)
        {
            closed.Should().ContainSingle().Which.Should().Be(("owner/repo", 42));
        }
        else
        {
            closed.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task RefreshAsync_ShouldContinue_WhenPullRequestClosedHandlerThrows()
    {
        await using ReviewEventCleanupCoordinator coordinator = CreateCoordinator(new StubStatusClient(PullRequestLifecycleState.Merged));
        coordinator.PullRequestClosed += (_, _) => throw new InvalidOperationException("handler failed");
        coordinator.Track(CreateReviewEvent());

        Func<Task> act = () => coordinator.RefreshAsync();

        await act.Should().NotThrowAsync();
        coordinator.TrackedEventCount.Should().Be(0);
        string log = await File.ReadAllTextAsync(Path.Combine(_logDirectory, "winui3.log"));
        log.Should().Contain("PR 完了の通知処理に失敗しました (owner/repo#42): handler failed");
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

    [Fact]
    public async Task RefreshAsync_ShouldQueryOncePerPullRequestAndRemoveAllItsEvents()
    {
        var statusClient = new RecordingStatusClient((_, prNumber) =>
            prNumber == 42 ? PullRequestLifecycleState.Merged : PullRequestLifecycleState.Open);
        await using ReviewEventCleanupCoordinator coordinator = CreateCoordinator(statusClient);
        List<string> removedIds = new();
        coordinator.EventsRemoved += (_, args) => removedIds.AddRange(args.EventIds);
        coordinator.Track(CreateReviewEvent("opened"));
        coordinator.Track(CreateReviewEvent("synchronized"));
        coordinator.Track(CreateReviewEvent("re-review", repository: "Owner/Repo"));
        coordinator.Track(CreateReviewEvent("other", 43));

        await coordinator.RefreshAsync();

        statusClient.Calls.Select(call => call.PrNumber).Should().BeEquivalentTo([42, 43]);
        removedIds.Should().BeEquivalentTo(["opened", "synchronized", "re-review"]);
        coordinator.TrackedEventCount.Should().Be(1);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 1)]
    [InlineData(20, 1)]
    [InlineData(20, 5)]
    public async Task RefreshAsync_ShouldNotExceedHourlyBudget_AndCheckEveryPullRequest(
        int pullRequestCount,
        int windowShowIntervalMinutes)
    {
        var timeProvider = new ManualTimeProvider();
        var statusClient = new RecordingStatusClient((_, _) => PullRequestLifecycleState.Open, timeProvider);
        await using ReviewEventCleanupCoordinator coordinator = CreateCoordinator(statusClient, timeProvider: timeProvider);
        for (int prNumber = 1; prNumber <= pullRequestCount; prNumber++)
        {
            coordinator.Track(CreateReviewEvent($"evt_{prNumber}", prNumber));
        }

        // 巡回（5 分ごと）とウィンドウ表示時の refresh を 3 時間分再現する
        for (int minute = 0; minute < 180; minute++)
        {
            if (minute % 5 == 0 || minute % windowShowIntervalMinutes == 0)
            {
                await coordinator.RefreshAsync();
            }

            timeProvider.Advance(TimeSpan.FromMinutes(1));
        }

        DateTimeOffset[] callTimes = statusClient.Calls.Select(call => call.At).ToArray();
        callTimes.Max(start => callTimes.Count(at => at >= start && at < start.AddHours(1))).Should().BeLessThanOrEqualTo(30);
        DateTimeOffset lastHour = timeProvider.GetUtcNow().AddHours(-1);
        statusClient.Calls.Where(call => call.At >= lastHour).Select(call => call.PrNumber).Distinct()
            .Should().HaveCount(pullRequestCount);
    }

    [Fact]
    public async Task RefreshAsync_ShouldPauseUntilResetTime_WhenRateLimited()
    {
        var timeProvider = new ManualTimeProvider();
        DateTimeOffset resetAt = timeProvider.GetUtcNow().AddMinutes(30);
        var statusClient = new RecordingStatusClient(
            (_, _) => PullRequestLifecycleState.Open,
            timeProvider,
            failFirstCallWith: new GitHubRateLimitException("rate limit exceeded", resetAt));
        await using ReviewEventCleanupCoordinator coordinator = CreateCoordinator(statusClient, timeProvider: timeProvider);
        coordinator.Track(CreateReviewEvent("evt_1", 1));
        coordinator.Track(CreateReviewEvent("evt_2", 2));

        await coordinator.RefreshAsync();
        timeProvider.Advance(TimeSpan.FromMinutes(29));
        await coordinator.RefreshAsync();

        statusClient.Calls.Should().ContainSingle();
        coordinator.TrackedEventCount.Should().Be(2);
        string log = await File.ReadAllTextAsync(Path.Combine(_logDirectory, "winui3.log"));
        log.Should().Contain("PR 状態の確認を停止します").And.Contain("rate limit exceeded");

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await coordinator.RefreshAsync();

        statusClient.Calls.Should().HaveCount(3);
    }

    [Fact]
    public async Task IsActionAllowedAsync_ShouldAllowWithoutLookup_WhileRateLimited()
    {
        var timeProvider = new ManualTimeProvider();
        var statusClient = new RecordingStatusClient(
            (_, _) => PullRequestLifecycleState.Merged,
            timeProvider,
            failFirstCallWith: new GitHubRateLimitException("rate limit exceeded", timeProvider.GetUtcNow().AddMinutes(30)));
        await using ReviewEventCleanupCoordinator coordinator = CreateCoordinator(statusClient, timeProvider: timeProvider);

        bool firstAllowed = await coordinator.IsActionAllowedAsync(CreateReviewEvent(), CancellationToken.None);
        bool secondAllowed = await coordinator.IsActionAllowedAsync(CreateReviewEvent(), CancellationToken.None);

        firstAllowed.Should().BeTrue();
        secondAllowed.Should().BeTrue();
        statusClient.Calls.Should().ContainSingle();
        coordinator.TrackedEventCount.Should().Be(1);
        string log = await File.ReadAllTextAsync(Path.Combine(_logDirectory, "winui3.log"));
        log.Should().Contain("PR 状態を確認せずに操作を許可します");
    }

    [Fact]
    public async Task IsActionAllowedAsync_ShouldKeepLaterResetTime_WhenShorterResetFollowsLongerResetInFlight()
    {
        var timeProvider = new ManualTimeProvider();
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset longResetAt = now.AddMinutes(60);
        DateTimeOffset shortResetAt = now.AddMinutes(10);

        var firstCallStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCallStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstCall = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondCall = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        int callCount = 0;
        var statusClient = new RecordingStatusClient(
            async (_, _) =>
            {
                int currentCall = Interlocked.Increment(ref callCount);
                if (currentCall == 1)
                {
                    firstCallStarted.SetResult(true);
                    await releaseFirstCall.Task;
                    throw new GitHubRateLimitException("rate limit 60m", longResetAt);
                }

                if (currentCall == 2)
                {
                    secondCallStarted.SetResult(true);
                    await releaseSecondCall.Task;
                    throw new GitHubRateLimitException("rate limit 10m", shortResetAt);
                }

                return PullRequestLifecycleState.Open;
            },
            timeProvider);

        await using ReviewEventCleanupCoordinator coordinator = CreateCoordinator(statusClient, timeProvider: timeProvider);

        Task<bool> task1 = coordinator.IsActionAllowedAsync(CreateReviewEvent("evt_1", 1), CancellationToken.None);
        await firstCallStarted.Task;

        Task<bool> task2 = coordinator.IsActionAllowedAsync(CreateReviewEvent("evt_2", 2), CancellationToken.None);
        await secondCallStarted.Task;

        releaseFirstCall.SetResult(true);
        bool result1 = await task1;
        result1.Should().BeTrue();

        releaseSecondCall.SetResult(true);
        bool result2 = await task2;
        result2.Should().BeTrue();

        timeProvider.Advance(TimeSpan.FromMinutes(29));

        bool allowedDuringLongPause = await coordinator.IsActionAllowedAsync(CreateReviewEvent("evt_3", 3), CancellationToken.None);
        allowedDuringLongPause.Should().BeTrue();
        statusClient.Calls.Should().HaveCount(2);

        timeProvider.Advance(TimeSpan.FromMinutes(32));

        await coordinator.RefreshAsync();
        statusClient.Calls.Should().HaveCount(5);
    }

    [Fact]
    public async Task IsActionAllowedAsync_ShouldLookUpEvenWhenRefreshBudgetIsExhausted()
    {
        var timeProvider = new ManualTimeProvider();
        var statusClient = new RecordingStatusClient((_, _) => PullRequestLifecycleState.Merged, timeProvider);
        await using ReviewEventCleanupCoordinator coordinator = CreateCoordinator(statusClient, timeProvider: timeProvider);
        for (int prNumber = 1; prNumber <= 6; prNumber++)
        {
            coordinator.Track(CreateReviewEvent($"evt_{prNumber}", prNumber));
        }

        await coordinator.RefreshAsync();
        statusClient.Calls.Should().HaveCount(5);

        bool allowed = await coordinator.IsActionAllowedAsync(CreateReviewEvent("evt_6", 6), CancellationToken.None);

        allowed.Should().BeFalse();
        statusClient.Calls.Should().HaveCount(6);
    }

    private ReviewEventCleanupCoordinator CreateCoordinator(
        IPullRequestStatusClient statusClient,
        TimeSpan? pollInterval = null,
        TimeProvider? timeProvider = null)
        => new(statusClient, _loggingService, pollInterval, timeProvider);

    private static ReviewEvent CreateReviewEvent(string eventId = "evt_1", int prNumber = 42, string repository = "owner/repo")
        => new()
        {
            EventId = eventId,
            Repository = repository,
            PrNumber = prNumber,
            PrUrl = $"https://github.com/{repository}/pull/{prNumber}",
            Reason = "opened",
            Message = "Review requested",
        };

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class RecordingStatusClient : IPullRequestStatusClient
    {
        private readonly Func<string, int, PullRequestLifecycleState>? _resolveState;
        private readonly Func<string, int, Task<PullRequestLifecycleState>>? _resolveStateAsync;
        private readonly TimeProvider _timeProvider;
        private Exception? _failFirstCallWith;

        public RecordingStatusClient(
            Func<string, int, PullRequestLifecycleState> resolveState,
            TimeProvider? timeProvider = null,
            Exception? failFirstCallWith = null)
        {
            _resolveState = resolveState;
            _timeProvider = timeProvider ?? TimeProvider.System;
            _failFirstCallWith = failFirstCallWith;
        }

        public RecordingStatusClient(
            Func<string, int, Task<PullRequestLifecycleState>> resolveStateAsync,
            TimeProvider? timeProvider = null)
        {
            _resolveStateAsync = resolveStateAsync;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        public List<(int PrNumber, DateTimeOffset At)> Calls { get; } = new();

        public async Task<PullRequestLifecycleState> GetStateAsync(string repository, int prNumber, CancellationToken cancellationToken)
        {
            Calls.Add((prNumber, _timeProvider.GetUtcNow()));
            if (_failFirstCallWith is Exception exception)
            {
                _failFirstCallWith = null;
                throw exception;
            }

            if (_resolveStateAsync is not null)
            {
                return await _resolveStateAsync(repository, prNumber).ConfigureAwait(false);
            }

            return _resolveState!(repository, prNumber);
        }
    }

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
