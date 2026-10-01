// <copyright file="ReviewStatusServiceTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text.Json.Nodes;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class ReviewStatusServiceTests : IDisposable
{
    private static readonly DateTimeOffset _initialTime = new(2026, 10, 1, 1, 0, 0, TimeSpan.Zero);
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ReviewStatusServiceTests_{Guid.NewGuid():D}");
    private readonly MutableTimeProvider _timeProvider = new(_initialTime);
    private readonly LoggingService _loggingService;
    private readonly ReviewCycleCoordinator _reviewCycleCoordinator;
    private readonly ReviewEventCleanupCoordinator _cleanupCoordinator;
    private readonly StubStatusClient _statusClient = new();
    private bool _autoStartEnabled = true;
    private SubscriptionState _subscriptionState = SubscriptionState.Running;
    private bool _authenticationRequired;

    public ReviewStatusServiceTests()
    {
        _loggingService = new LoggingService(LogDirectory);
        _reviewCycleCoordinator = new ReviewCycleCoordinator(
            new ReviewCycleStore(Path.Combine(_testDirectory, "cycles"), _timeProvider),
            _loggingService,
            _timeProvider);
        _cleanupCoordinator = new ReviewEventCleanupCoordinator(_statusClient, _loggingService, timeProvider: _timeProvider);
    }

    private string LogDirectory => Path.Combine(_testDirectory, "logs");

    private string StatusPath => Path.Combine(_testDirectory, ReviewStatusService.FileName);

    public void Dispose()
    {
        _cleanupCoordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsync_ShouldWriteEmptyDocumentWithEnvironmentAndSchemaVersion()
    {
        ReviewStatusService service = CreateService();

        await service.StartAsync();

        JsonNode status = await ReadStatusAsync();
        status["schemaVersion"]!.GetValue<int>().Should().Be(1);
        status["updatedAt"]!.GetValue<string>().Should().Be("2026-10-01T01:00:00Z");
        status["app"]!["version"]!.GetValue<string>().Should().Be("0.15.0");
        status["app"]!["startedAt"]!.GetValue<string>().Should().Be("2026-10-01T01:00:00Z");
        status["concurrency"]!["active"]!.GetValue<int>().Should().Be(0);
        status["concurrency"]!["max"]!.GetValue<int>().Should().Be(1);
        status["subscription"]!["state"]!.GetValue<string>().Should().Be("running");
        status["items"]!.AsArray().Should().BeEmpty();
        status["recent"]!.AsArray().Should().BeEmpty();
        File.Exists(StatusPath + ".tmp").Should().BeFalse();
    }

    [Fact]
    public async Task ApplyStateAsync_ShouldFollowWaitingRunningAndFinished()
    {
        ReviewStatusService service = CreateService();
        ReviewEvent reviewEvent = CreateReviewEvent("event-opened", receivedAt: _initialTime.AddMinutes(-5));

        await service.ApplyStateAsync(reviewEvent, CreateState(status: ReviewCycleStatus.AwaitingReviewer, lastEventId: "event-opened"));
        JsonNode waiting = (await ReadStatusAsync())["items"]![0]!;
        waiting["key"]!.GetValue<string>().Should().Be("owner/repo#42");
        waiting["repository"]!.GetValue<string>().Should().Be("owner/Repo");
        waiting["state"]!.GetValue<string>().Should().Be("waiting");
        waiting["eventId"]!.GetValue<string>().Should().Be("event-opened");
        waiting["receivedAt"]!.GetValue<string>().Should().Be("2026-10-01T00:55:00Z");

        _timeProvider.UtcNow = _initialTime.AddMinutes(2);
        await service.ApplyStateAsync(
            reviewEvent,
            CreateState(
                status: ReviewCycleStatus.ReviewerRunning,
                lastEventId: "event-opened",
                activeEventId: "event-opened",
                activeAgent: "codex",
                updatedAt: _timeProvider.UtcNow));
        JsonNode status = await ReadStatusAsync();
        JsonNode running = status["items"]![0]!;
        running["state"]!.GetValue<string>().Should().Be("running");
        running["agent"]!.GetValue<string>().Should().Be("codex");
        running["receivedAt"]!.GetValue<string>().Should().Be("2026-10-01T00:55:00Z");
        running["startedAt"]!.GetValue<string>().Should().Be("2026-10-01T01:02:00Z");
        status["concurrency"]!["active"]!.GetValue<int>().Should().Be(1);

        _timeProvider.UtcNow = _initialTime.AddMinutes(12);
        await service.ApplyStateAsync(
            reviewEvent,
            CreateState(status: ReviewCycleStatus.ReviewerCompleted, lastEventId: "event-opened", updatedAt: _timeProvider.UtcNow),
            exitCode: 0);
        status = await ReadStatusAsync();
        status["items"]!.AsArray().Should().BeEmpty();
        status["concurrency"]!["active"]!.GetValue<int>().Should().Be(0);
        JsonNode finished = status["recent"]![0]!;
        finished["state"]!.GetValue<string>().Should().Be("finished");
        finished["agent"]!.GetValue<string>().Should().Be("codex");
        finished["receivedAt"]!.GetValue<string>().Should().Be("2026-10-01T00:55:00Z");
        finished["startedAt"]!.GetValue<string>().Should().Be("2026-10-01T01:02:00Z");
        finished["finishedAt"]!.GetValue<string>().Should().Be("2026-10-01T01:12:00Z");
        finished["outcome"]!.GetValue<string>().Should().Be("completed");
        finished["exitCode"]!.GetValue<int>().Should().Be(0);
    }

    [Theory]
    [InlineData("ReviewerCompleted", "completed", 0)]
    [InlineData("ReviewerFailed", "failed", 1)]
    [InlineData("ReviewerFailed", "failed", -1)]
    public async Task ApplyStateAsync_ShouldRecordOutcomeAndExitCode(string status, string expectedOutcome, int exitCode)
    {
        ReviewStatusService service = CreateService();
        ReviewEvent reviewEvent = CreateReviewEvent("event-opened");
        await service.ApplyStateAsync(
            reviewEvent,
            CreateState(status: ReviewCycleStatus.ReviewerRunning, lastEventId: "event-opened", activeEventId: "event-opened"));

        await service.ApplyStateAsync(
            reviewEvent,
            CreateState(status: Enum.Parse<ReviewCycleStatus>(status), lastEventId: "event-opened", updatedAt: _initialTime.AddMinutes(1)),
            exitCode);

        JsonNode finished = (await ReadStatusAsync())["recent"]![0]!;
        finished["outcome"]!.GetValue<string>().Should().Be(expectedOutcome);
        finished["exitCode"]!.GetValue<int>().Should().Be(exitCode);
    }

    [Fact]
    public async Task ApplyStateAsync_ShouldOmitExitCode_WhenProcessDidNotReportOne()
    {
        ReviewStatusService service = CreateService();
        ReviewEvent reviewEvent = CreateReviewEvent("event-opened");

        await service.ApplyStateAsync(
            reviewEvent,
            CreateState(status: ReviewCycleStatus.ReviewerFailed, lastEventId: "event-opened"),
            exitCode: null);

        JsonObject finished = (await ReadStatusAsync())["recent"]![0]!.AsObject();
        finished["outcome"]!.GetValue<string>().Should().Be("failed");
        finished.ContainsKey("exitCode").Should().BeFalse();
    }

    [Fact]
    public async Task ApplyStateAsync_ShouldOmitFieldsThatDoNotApply()
    {
        ReviewStatusService service = CreateService();

        await service.ApplyStateAsync(
            CreateReviewEvent("event-opened"),
            CreateState(status: ReviewCycleStatus.AwaitingReviewer, lastEventId: "event-opened"));

        JsonObject waiting = (await ReadStatusAsync())["items"]![0]!.AsObject();
        waiting.ContainsKey("startedAt").Should().BeFalse();
        waiting.ContainsKey("finishedAt").Should().BeFalse();
        waiting.ContainsKey("outcome").Should().BeFalse();
        waiting.ContainsKey("exitCode").Should().BeFalse();
        waiting.ContainsKey("agent").Should().BeFalse();
        waiting.ContainsKey("queuePosition").Should().BeFalse();
        waiting.ContainsKey("holdReason").Should().BeFalse();
    }

    [Fact]
    public async Task ApplyStateAsync_ShouldIgnoreUnknownStatus()
    {
        ReviewStatusService service = CreateService();

        await service.ApplyStateAsync(CreateReviewEvent("event-opened"), CreateState(status: ReviewCycleStatus.Unknown));

        File.Exists(StatusPath).Should().BeFalse();
    }

    [Fact]
    public async Task ApplyStateAsync_ShouldIgnoreStateOlderThanCurrent()
    {
        ReviewStatusService service = CreateService();
        ReviewEvent reviewEvent = CreateReviewEvent("event-opened");
        await service.ApplyStateAsync(
            reviewEvent,
            CreateState(
                status: ReviewCycleStatus.ReviewerRunning,
                lastEventId: "event-opened",
                activeEventId: "event-opened",
                updatedAt: _initialTime.AddMinutes(1)));

        await service.ApplyStateAsync(
            reviewEvent,
            CreateState(status: ReviewCycleStatus.AwaitingReviewer, lastEventId: "event-opened", updatedAt: _initialTime));

        JsonNode status = await ReadStatusAsync();
        status["items"]!.AsArray().Should().ContainSingle();
        status["items"]![0]!["state"]!.GetValue<string>().Should().Be("running");
    }

    [Fact]
    public async Task ApplyHoldAsync_ShouldDescribeHoldReasonAndQueuePositionInHoldOrder()
    {
        ReviewStatusService service = CreateService();
        ReviewEvent first = CreateReviewEvent("event-1", prNumber: 1);
        ReviewEvent second = CreateReviewEvent("event-2", prNumber: 2);
        await service.ApplyStateAsync(first, CreateState(prNumber: 1, lastEventId: "event-1"));
        await service.ApplyStateAsync(second, CreateState(prNumber: 2, lastEventId: "event-2"));

        _timeProvider.UtcNow = _initialTime.AddMinutes(1);
        await service.ApplyHoldAsync(second, ReviewHoldKind.CiPending);
        _timeProvider.UtcNow = _initialTime.AddMinutes(2);
        await service.ApplyHoldAsync(first, ReviewHoldKind.Busy);

        JsonArray items = (await ReadStatusAsync())["items"]!.AsArray();
        items.Select(static item => item!["prNumber"]!.GetValue<int>()).Should().Equal(2, 1);
        items.Select(static item => item!["holdReason"]!.GetValue<string>()).Should().Equal("ciPending", "busy");
        items.Select(static item => item!["queuePosition"]!.GetValue<int>()).Should().Equal(1, 2);
        items[0]!["holdSince"]!.GetValue<string>().Should().Be("2026-10-01T01:01:00Z");
    }

    [Fact]
    public async Task ApplyHoldAsync_ShouldKeepHoldSinceWhenReasonChanges()
    {
        ReviewStatusService service = CreateService();
        ReviewEvent reviewEvent = CreateReviewEvent("event-opened");
        await service.ApplyStateAsync(reviewEvent, CreateState(lastEventId: "event-opened"));
        _timeProvider.UtcNow = _initialTime.AddMinutes(1);
        await service.ApplyHoldAsync(reviewEvent, ReviewHoldKind.Busy);

        _timeProvider.UtcNow = _initialTime.AddMinutes(9);
        await service.ApplyHoldAsync(reviewEvent, ReviewHoldKind.CiPending);

        JsonNode item = (await ReadStatusAsync())["items"]![0]!;
        item["holdReason"]!.GetValue<string>().Should().Be("ciPending");
        item["holdSince"]!.GetValue<string>().Should().Be("2026-10-01T01:01:00Z");
    }

    [Fact]
    public async Task ApplyStateAsync_ShouldKeepHoldWhenNewerEventReplacesWaitingEntry()
    {
        ReviewStatusService service = CreateService();
        ReviewEvent opened = CreateReviewEvent("event-opened");
        await service.ApplyStateAsync(opened, CreateState(lastEventId: "event-opened"));
        _timeProvider.UtcNow = _initialTime.AddMinutes(1);
        await service.ApplyHoldAsync(opened, ReviewHoldKind.Busy);

        ReviewEvent reReview = CreateReviewEvent("event-re-review", reason: "re-review-requested");
        await service.ApplyStateAsync(
            reReview,
            CreateState(
                reason: "re-review-requested",
                round: 2,
                lastEventId: "event-re-review",
                updatedAt: _initialTime.AddMinutes(3)));

        JsonNode item = (await ReadStatusAsync())["items"]![0]!;
        item["eventId"]!.GetValue<string>().Should().Be("event-re-review");
        item["round"]!.GetValue<int>().Should().Be(2);
        item["holdReason"]!.GetValue<string>().Should().Be("busy");
        item["holdSince"]!.GetValue<string>().Should().Be("2026-10-01T01:01:00Z");
    }

    // 実行中（event A）に、同じ PR の次の event（B）が届くと、実行中の状態（ActiveEventId = A）に、
    // B の LastEventId・LastReason・受信時刻を載せて通知される。実行中の A の情報を、B で上書きしない
    [Fact]
    public async Task ApplyStateAsync_ShouldKeepRunningEventMetadata_WhenNextEventArrivesDuringExecution()
    {
        ReviewStatusService service = CreateService();
        ReviewEvent eventA = CreateReviewEvent("event-a", reason: "opened", receivedAt: _initialTime.AddMinutes(-5));
        await service.ApplyStateAsync(
            eventA,
            CreateState(
                status: ReviewCycleStatus.ReviewerRunning,
                lastEventId: "event-a",
                activeEventId: "event-a",
                activeAgent: "codex",
                updatedAt: _initialTime));

        _timeProvider.UtcNow = _initialTime.AddMinutes(4);
        ReviewEvent eventB = CreateReviewEvent("event-b", reason: "re-review-requested", receivedAt: _timeProvider.UtcNow);
        await service.ApplyStateAsync(
            eventB,
            CreateState(
                status: ReviewCycleStatus.ReviewerRunning,
                reason: "re-review-requested",
                round: 2,
                lastEventId: "event-b",
                activeEventId: "event-a",
                activeAgent: "codex",
                updatedAt: _timeProvider.UtcNow));

        JsonNode running = (await ReadStatusAsync())["items"]![0]!;
        running["eventId"]!.GetValue<string>().Should().Be("event-a");
        running["reason"]!.GetValue<string>().Should().Be("opened");
        running["round"]!.GetValue<int>().Should().Be(1);
        running["receivedAt"]!.GetValue<string>().Should().Be("2026-10-01T00:55:00Z");
        running["startedAt"]!.GetValue<string>().Should().Be("2026-10-01T01:00:00Z");
        running["agent"]!.GetValue<string>().Should().Be("codex");

        _timeProvider.UtcNow = _initialTime.AddMinutes(10);
        await service.ApplyStateAsync(
            eventA,
            CreateState(
                status: ReviewCycleStatus.ReviewerCompleted,
                lastEventId: "event-a",
                updatedAt: _timeProvider.UtcNow),
            exitCode: 0);

        JsonNode finished = (await ReadStatusAsync())["recent"]![0]!;
        finished["eventId"]!.GetValue<string>().Should().Be("event-a");
        finished["reason"]!.GetValue<string>().Should().Be("opened");
        finished["receivedAt"]!.GetValue<string>().Should().Be("2026-10-01T00:55:00Z");
        finished["startedAt"]!.GetValue<string>().Should().Be("2026-10-01T01:00:00Z");
        finished["finishedAt"]!.GetValue<string>().Should().Be("2026-10-01T01:10:00Z");
        finished["agent"]!.GetValue<string>().Should().Be("codex");
    }

    [Fact]
    public async Task ApplyStateAsync_ShouldNotResetStartedAt_WhenSameRunningStateIsPublishedAgain()
    {
        ReviewStatusService service = CreateService();
        ReviewEvent eventA = CreateReviewEvent("event-a");
        await service.ApplyStateAsync(
            eventA,
            CreateState(status: ReviewCycleStatus.ReviewerRunning, lastEventId: "event-a", activeEventId: "event-a", updatedAt: _initialTime));

        // 重複した event の受信で、既存の状態が再通知される
        await service.ApplyStateAsync(
            eventA,
            CreateState(status: ReviewCycleStatus.ReviewerRunning, lastEventId: "event-a", activeEventId: "event-a", updatedAt: _initialTime.AddMinutes(3)));

        (await ReadStatusAsync())["items"]![0]!["startedAt"]!.GetValue<string>().Should().Be("2026-10-01T01:00:00Z");
    }

    // A の実行中に、次の event（B）が busy で保留されると、その時点では B の起動待ちの項目が無い。
    // A の終了後に B が起動待ちになるとき、実行中に観測した保留の理由と待ち順を引き継ぐ
    [Fact]
    public async Task ApplyHoldAsync_ShouldCarryHoldOfNextEventToWaitingStateAfterRunningCompletes()
    {
        ReviewStatusService service = CreateService();
        ReviewEvent eventA = CreateReviewEvent("event-a", reason: "opened");
        await service.ApplyStateAsync(
            eventA,
            CreateState(
                status: ReviewCycleStatus.ReviewerRunning,
                lastEventId: "event-a",
                activeEventId: "event-a",
                activeAgent: "codex",
                updatedAt: _initialTime));
        _timeProvider.UtcNow = _initialTime.AddMinutes(4);
        ReviewEvent eventB = CreateReviewEvent("event-b", reason: "re-review-requested", receivedAt: _timeProvider.UtcNow);
        await service.ApplyStateAsync(
            eventB,
            CreateState(
                status: ReviewCycleStatus.ReviewerRunning,
                reason: "re-review-requested",
                round: 2,
                lastEventId: "event-b",
                activeEventId: "event-a",
                activeAgent: "codex",
                updatedAt: _timeProvider.UtcNow));
        _timeProvider.UtcNow = _initialTime.AddMinutes(5);
        await service.ApplyHoldAsync(eventB, ReviewHoldKind.Busy);
        (await ReadStatusAsync())["items"]!.AsArray().Should().ContainSingle().Which!["state"]!.GetValue<string>().Should().Be("running");

        _timeProvider.UtcNow = _initialTime.AddMinutes(10);
        await service.ApplyStateAsync(
            eventA,
            CreateState(status: ReviewCycleStatus.ReviewerCompleted, lastEventId: "event-b", updatedAt: _timeProvider.UtcNow),
            exitCode: 0);
        _timeProvider.UtcNow = _initialTime.AddMinutes(10).AddSeconds(1);
        await service.ApplyStateAsync(
            eventB,
            CreateState(
                status: ReviewCycleStatus.AwaitingReviewer,
                reason: "re-review-requested",
                round: 2,
                lastEventId: "event-b",
                updatedAt: _timeProvider.UtcNow));

        JsonNode status = await ReadStatusAsync();
        JsonNode waiting = status["items"]![0]!;
        waiting["state"]!.GetValue<string>().Should().Be("waiting");
        waiting["eventId"]!.GetValue<string>().Should().Be("event-b");
        waiting["round"]!.GetValue<int>().Should().Be(2);
        waiting["holdReason"]!.GetValue<string>().Should().Be("busy");
        waiting["holdSince"]!.GetValue<string>().Should().Be("2026-10-01T01:05:00Z");
        waiting["queuePosition"]!.GetValue<int>().Should().Be(1);
        status["recent"]![0]!["eventId"]!.GetValue<string>().Should().Be("event-a");
    }

    [Fact]
    public async Task ApplyHoldAsync_ShouldApplyHoldObservedBeforeTheWaitingStateIsPublished()
    {
        ReviewStatusService service = CreateService();
        ReviewEvent reviewEvent = CreateReviewEvent("event-opened");
        _timeProvider.UtcNow = _initialTime.AddMinutes(1);
        await service.ApplyHoldAsync(reviewEvent, ReviewHoldKind.CiPending);

        await service.ApplyStateAsync(
            reviewEvent,
            CreateState(lastEventId: "event-opened", updatedAt: _initialTime.AddMinutes(2)));

        JsonNode item = (await ReadStatusAsync())["items"]![0]!;
        item["holdReason"]!.GetValue<string>().Should().Be("ciPending");
        item["holdSince"]!.GetValue<string>().Should().Be("2026-10-01T01:01:00Z");
    }

    [Fact]
    public async Task ApplyHoldAsync_ShouldNotRewriteNewerWaitingEntry_WhenHoldOfOlderEventArrivesLate()
    {
        ReviewStatusService service = CreateService();
        ReviewEvent older = CreateReviewEvent("event-old");
        await service.ApplyStateAsync(older, CreateState(lastEventId: "event-old"));
        await service.ApplyHoldAsync(older, ReviewHoldKind.Busy);
        ReviewEvent newer = CreateReviewEvent("event-new", reason: "re-review-requested");
        await service.ApplyStateAsync(
            newer,
            CreateState(reason: "re-review-requested", round: 2, lastEventId: "event-new", updatedAt: _initialTime.AddMinutes(1)));

        await service.ApplyHoldAsync(older, ReviewHoldKind.CiPending);

        JsonNode item = (await ReadStatusAsync())["items"]![0]!;
        item["eventId"]!.GetValue<string>().Should().Be("event-new");
        item["holdReason"]!.GetValue<string>().Should().Be("busy");
    }

    [Fact]
    public async Task ApplyHoldAsync_ShouldIgnoreHoldWithoutWaitingEntry()
    {
        ReviewStatusService service = CreateService();
        ReviewEvent reviewEvent = CreateReviewEvent("event-opened");
        await service.ApplyStateAsync(
            reviewEvent,
            CreateState(status: ReviewCycleStatus.ReviewerRunning, lastEventId: "event-opened", activeEventId: "event-opened"));

        await service.ApplyHoldAsync(reviewEvent, ReviewHoldKind.Busy);
        await service.ApplyHoldAsync(CreateReviewEvent("event-other", prNumber: 99), ReviewHoldKind.Busy);

        JsonNode status = await ReadStatusAsync();
        status["items"]!.AsArray().Should().ContainSingle();
        status["items"]![0]!.AsObject().ContainsKey("holdReason").Should().BeFalse();
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "manual")]
    public async Task ApplyStateAsync_ShouldDescribeManualOperation_WhenAutoStartIsOffAndNoHoldObserved(
        bool autoStartEnabled,
        string? expectedHoldReason)
    {
        _autoStartEnabled = autoStartEnabled;
        ReviewStatusService service = CreateService();

        await service.ApplyStateAsync(CreateReviewEvent("event-opened"), CreateState(lastEventId: "event-opened"));

        JsonObject item = (await ReadStatusAsync())["items"]![0]!.AsObject();
        if (expectedHoldReason is null)
        {
            item.ContainsKey("holdReason").Should().BeFalse();
        }
        else
        {
            item["holdReason"]!.GetValue<string>().Should().Be(expectedHoldReason);
            item["holdSince"]!.GetValue<string>().Should().Be("2026-10-01T00:00:00Z");
            item.ContainsKey("queuePosition").Should().BeFalse();
        }
    }

    [Theory]
    [InlineData("event-last", 0, 0)]
    [InlineData("event-active", 0, 0)]
    [InlineData("event-other", 1, 0)]
    public async Task RemoveEventsAsync_ShouldDropRunningPullRequestOfRemovedEvent(
        string removedEventId,
        int expectedItems,
        int expectedRecent)
    {
        ReviewStatusService service = CreateService();
        await service.ApplyStateAsync(
            CreateReviewEvent("event-active"),
            CreateState(status: ReviewCycleStatus.ReviewerRunning, lastEventId: "event-last", activeEventId: "event-active"));

        await service.RemoveEventsAsync([removedEventId]);

        JsonNode status = await ReadStatusAsync();
        status["items"]!.AsArray().Should().HaveCount(expectedItems);
        status["recent"]!.AsArray().Should().HaveCount(expectedRecent);
    }

    [Fact]
    public async Task RemoveEventsAsync_ShouldDropFinishedReviewOfRemovedEvent()
    {
        ReviewStatusService service = CreateService();
        await service.ApplyStateAsync(
            CreateReviewEvent("event-opened"),
            CreateState(status: ReviewCycleStatus.ReviewerCompleted, lastEventId: "event-opened"),
            exitCode: 0);

        await service.RemoveEventsAsync(["event-opened"]);

        (await ReadStatusAsync())["recent"]!.AsArray().Should().BeEmpty();
    }

    // reviewer 実行中に次のイベントが届き、その PR が終了済みとして削除された後で reviewer が終了すると、
    // coordinator は削除済みイベントの待機状態を発行する。これで再登録しない。再オープン後の新しいイベントは反映する
    [Theory]
    [InlineData("event-re-review", 0)]
    [InlineData("event-reopened", 1)]
    public async Task ApplyStateAsync_ShouldNotRestorePullRequestOfRemovedEvent(string lastEventIdAfterRemoval, int expectedWaiting)
    {
        ReviewStatusService service = CreateService();
        await service.ApplyStateAsync(
            CreateReviewEvent("event-opened"),
            CreateState(
                status: ReviewCycleStatus.ReviewerRunning,
                lastEventId: "event-re-review",
                activeEventId: "event-opened"));
        await service.RemoveEventsAsync(["event-opened", "event-re-review"]);

        await service.ApplyStateAsync(
            CreateReviewEvent(lastEventIdAfterRemoval),
            CreateState(status: ReviewCycleStatus.AwaitingReviewer, lastEventId: lastEventIdAfterRemoval, updatedAt: _initialTime.AddMinutes(1)));

        JsonNode status = await ReadStatusAsync();
        status["items"]!.AsArray().Should().HaveCount(expectedWaiting);
        status["concurrency"]!["active"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task RefreshAsync_ShouldUpdateUpdatedAtWithoutAnyStateChange()
    {
        ReviewStatusService service = CreateService();
        await service.StartAsync();
        (await ReadStatusAsync())["updatedAt"]!.GetValue<string>().Should().Be("2026-10-01T01:00:00Z");

        _timeProvider.UtcNow = _initialTime.AddSeconds(60);
        await service.RefreshAsync();

        JsonNode status = await ReadStatusAsync();
        status["updatedAt"]!.GetValue<string>().Should().Be("2026-10-01T01:01:00Z");
        status["items"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_ShouldRefreshEveryHeartbeatInterval()
    {
        ReviewStatusService service = CreateService();
        await service.StartAsync();

        _timeProvider.Timers.Should().ContainSingle();
        _timeProvider.Timers[0].Period.Should().Be(ReviewStatusService.HeartbeatInterval).And.Be(TimeSpan.FromSeconds(60));

        _timeProvider.UtcNow = _initialTime.AddSeconds(60);
        _timeProvider.Timers[0].Fire();

        await WaitForStatusAsync(node => node["updatedAt"]!.GetValue<string>() == "2026-10-01T01:01:00Z");
    }

    [Theory]
    [InlineData("Running", false, "running")]
    [InlineData("Starting", false, "starting")]
    [InlineData("Stopping", false, "stopping")]
    [InlineData("Stopped", false, "stopped")]
    [InlineData("Error", false, "error")]
    [InlineData("Error", true, "authRequired")]
    [InlineData("Stopped", true, "authRequired")]
    [InlineData("Running", true, "running")]
    public async Task NotifySubscriptionChangedAsync_ShouldDescribeSubscriptionAndWhenItChanged(
        string state,
        bool authenticationRequired,
        string expectedState)
    {
        ReviewStatusService service = CreateService();
        await service.StartAsync();

        _timeProvider.UtcNow = _initialTime.AddMinutes(7);
        _subscriptionState = Enum.Parse<SubscriptionState>(state);
        _authenticationRequired = authenticationRequired;
        await service.NotifySubscriptionChangedAsync();

        JsonNode subscription = (await ReadStatusAsync())["subscription"]!;
        subscription["state"]!.GetValue<string>().Should().Be(expectedState);
        subscription["since"]!.GetValue<string>().Should().Be(expectedState == "running" ? "2026-10-01T01:00:00Z" : "2026-10-01T01:07:00Z");
    }

    [Fact]
    public async Task NotifySubscriptionChangedAsync_ShouldNotWrite_WhenDescribedStateIsUnchanged()
    {
        ReviewStatusService service = CreateService();
        await service.StartAsync();
        _timeProvider.UtcNow = _initialTime.AddMinutes(7);

        await service.NotifySubscriptionChangedAsync();

        (await ReadStatusAsync())["updatedAt"]!.GetValue<string>().Should().Be("2026-10-01T01:00:00Z");
    }

    [Fact]
    public async Task ShutdownAsync_ShouldDeleteStatusAndIgnoreLaterUpdates()
    {
        ReviewStatusService service = CreateService();
        await service.StartAsync();

        await service.ShutdownAsync();
        await service.ApplyStateAsync(CreateReviewEvent("event-opened"), CreateState(lastEventId: "event-opened"));

        File.Exists(StatusPath).Should().BeFalse();
        _timeProvider.Timers.Single().IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task ApplyStateAsync_ShouldLogAndKeepPreviousStatus_WhenReplaceIsBlocked()
    {
        ReviewStatusService service = CreateService();
        await service.StartAsync();

        using (new FileStream(StatusPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await service.ApplyStateAsync(CreateReviewEvent("event-opened"), CreateState(lastEventId: "event-opened"));
        }

        (await ReadStatusAsync())["items"]!.AsArray().Should().BeEmpty();
        string log = await File.ReadAllTextAsync(Path.Combine(LogDirectory, "winui3.log"));
        log.Should().Contain("[ReviewStatus] 公開状態を書き出せません");

        await service.ApplyStateAsync(
            CreateReviewEvent("event-other", prNumber: 43),
            CreateState(prNumber: 43, lastEventId: "event-other", updatedAt: _initialTime.AddMinutes(1)));
        (await ReadStatusAsync())["items"]!.AsArray().Should().HaveCount(2);
    }

    [Fact]
    public async Task ReviewCycleCoordinatorEvents_ShouldBeReflectedFromWaitingToFinishedWithExitCode()
    {
        CreateService();
        ReviewEvent reviewEvent = CreateReviewEvent("event-opened");

        await _reviewCycleCoordinator.ObserveEventAsync(reviewEvent);
        await WaitForStatusAsync(node => node["items"]!.AsArray().Count == 1);

        _reviewCycleCoordinator.ObserveHold(reviewEvent, ReviewHoldKind.Busy);
        JsonNode held = await WaitForStatusAsync(node => node["items"]![0]!.AsObject().ContainsKey("holdReason"));
        held["items"]![0]!["holdReason"]!.GetValue<string>().Should().Be("busy");
        held["items"]![0]!["queuePosition"]!.GetValue<int>().Should().Be(1);

        AgentExecutionSession session = new(TimeProvider.System);
        await _reviewCycleCoordinator.MarkReviewerStartedAsync(
            reviewEvent,
            new ReviewStartLaunch(session, null!, null!, null!),
            "codex");
        JsonNode running = await WaitForStatusAsync(node => node["concurrency"]!["active"]!.GetValue<int>() == 1);
        running["items"]![0]!["agent"]!.GetValue<string>().Should().Be("codex");
        running["items"]![0]!.AsObject().ContainsKey("holdReason").Should().BeFalse();

        session.Complete(AgentExecutionOutcome.Failed, new LauncherResult { Success = false, ExitCode = 3 });
        JsonNode finished = await WaitForStatusAsync(node => node["recent"]!.AsArray().Count == 1);
        finished["items"]!.AsArray().Should().BeEmpty();
        finished["recent"]![0]!["outcome"]!.GetValue<string>().Should().Be("failed");
        finished["recent"]![0]!["exitCode"]!.GetValue<int>().Should().Be(3);
        finished["recent"]![0]!["agent"]!.GetValue<string>().Should().Be("codex");
    }

    [Fact]
    public async Task EventsRemoved_ShouldDropClosedPullRequestFromStatus()
    {
        CreateService();
        ReviewEvent reviewEvent = CreateReviewEvent("event-opened");
        await _reviewCycleCoordinator.ObserveEventAsync(reviewEvent);
        await WaitForStatusAsync(node => node["items"]!.AsArray().Count == 1);
        _cleanupCoordinator.Track(reviewEvent);

        _statusClient.State = PullRequestLifecycleState.Closed;
        await _cleanupCoordinator.RefreshAsync();

        await WaitForStatusAsync(node => node["items"]!.AsArray().Count == 0);
    }

    private ReviewStatusService CreateService()
        => new(
            _testDirectory,
            _reviewCycleCoordinator,
            _cleanupCoordinator,
            _loggingService,
            "0.15.0",
            () => _autoStartEnabled,
            () => _subscriptionState,
            () => _authenticationRequired,
            _timeProvider);

    private async Task<JsonNode> ReadStatusAsync()
        => JsonNode.Parse(await File.ReadAllTextAsync(StatusPath))!;

    // 状態変化の通知経由の書き出しは待ち合わせる手段が無いため、内容が期待どおりになるまで待つ
    private async Task<JsonNode> WaitForStatusAsync(Func<JsonNode, bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (true)
        {
            if (File.Exists(StatusPath))
            {
                // 書き出しは一時ファイルからの置き換えで、置き換えと読み取りが重なると IOException になる。
                // 待機中の一時状態として扱い、次の周回で読み直す（#438 と同じ）.
                JsonNode? status = null;
                try
                {
                    status = await ReadStatusAsync();
                }
                catch (IOException)
                {
                }

                if (status is not null && predicate(status))
                {
                    return status;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }
    }

    private static ReviewCycleState CreateState(
        string repository = "owner/Repo",
        int prNumber = 42,
        ReviewCycleStatus status = ReviewCycleStatus.AwaitingReviewer,
        string reason = "opened",
        int round = 1,
        int? activeRound = null,
        string? activeAgent = null,
        string lastEventId = "event-last",
        string? activeEventId = null,
        DateTimeOffset? updatedAt = null)
        => new(
            repository,
            prNumber,
            round,
            lastEventId,
            reason,
            status,
            activeEventId,
            activeRound,
            updatedAt ?? _initialTime,
            [lastEventId],
            activeAgent);

    private static ReviewEvent CreateReviewEvent(
        string eventId,
        int prNumber = 42,
        string reason = "opened",
        DateTimeOffset? receivedAt = null)
        => new()
        {
            EventId = eventId,
            Repository = "owner/Repo",
            PrNumber = prNumber,
            PrUrl = $"https://github.com/owner/Repo/pull/{prNumber}",
            Reason = reason,
            Message = reason,
            ReceivedTime = (receivedAt ?? _initialTime.AddHours(-1)).UtcDateTime,
        };

    private sealed class StubStatusClient : IPullRequestStatusClient
    {
        public PullRequestLifecycleState State { get; set; } = PullRequestLifecycleState.Open;

        public Task<PullRequestLifecycleState> GetStateAsync(string repository, int prNumber, CancellationToken cancellationToken)
            => Task.FromResult(State);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public List<FakeTimer> Timers { get; } = [];

        public override DateTimeOffset GetUtcNow() => UtcNow;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            FakeTimer timer = new(callback, state, period);
            Timers.Add(timer);
            return timer;
        }
    }

    private sealed class FakeTimer(TimerCallback callback, object? state, TimeSpan period) : ITimer
    {
        public TimeSpan Period { get; } = period;

        public bool IsDisposed { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => IsDisposed = true;

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
