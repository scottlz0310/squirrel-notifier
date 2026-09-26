// <copyright file="StatuslineSummaryServiceTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text.Json.Nodes;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class StatuslineSummaryServiceTests : IDisposable
{
    private static readonly DateTimeOffset _initialTime = new(2026, 9, 26, 6, 20, 0, TimeSpan.Zero);
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        $"StatuslineSummaryServiceTests_{Guid.NewGuid():D}");
    private readonly MutableTimeProvider _timeProvider = new(_initialTime);
    private readonly LoggingService _loggingService;
    private readonly ReviewCycleCoordinator _reviewCycleCoordinator;
    private readonly ReviewEventCleanupCoordinator _cleanupCoordinator;
    private readonly StubStatusClient _statusClient = new();

    public StatuslineSummaryServiceTests()
    {
        _loggingService = new LoggingService(LogDirectory);
        _reviewCycleCoordinator = new ReviewCycleCoordinator(
            new ReviewCycleStore(Path.Combine(_testDirectory, "cycles"), _timeProvider),
            _loggingService,
            _timeProvider);
        _cleanupCoordinator = new ReviewEventCleanupCoordinator(_statusClient, _loggingService, timeProvider: _timeProvider);
    }

    private string LogDirectory => Path.Combine(_testDirectory, "logs");

    private string SummaryPath => Path.Combine(_testDirectory, StatuslineSummaryService.FileName);

    public void Dispose()
    {
        _cleanupCoordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsync_ShouldWriteEmptySummaryWithSchemaVersion()
    {
        StatuslineSummaryService service = CreateService();

        await service.StartAsync();

        JsonNode summary = await ReadSummaryAsync();
        summary["schemaVersion"]!.GetValue<int>().Should().Be(1);
        summary["updatedAt"]!.GetValue<string>().Should().Be("2026-09-26T06:20:00Z");
        summary["queue"]!["totalWaiting"]!.GetValue<int>().Should().Be(0);
        summary["queue"]!["items"]!.AsArray().Should().BeEmpty();
        summary["activeReviews"]!.AsArray().Should().BeEmpty();
        File.Exists(SummaryPath + ".tmp").Should().BeFalse();
    }

    [Theory]
    [InlineData("AwaitingReviewer", 1, 0)]
    [InlineData("ReviewerRunning", 0, 1)]
    [InlineData("ReviewerCompleted", 0, 0)]
    [InlineData("ReviewerFailed", 0, 0)]
    [InlineData("Unknown", 0, 0)]
    public async Task ApplyStateAsync_ShouldClassifyByCycleStatus(
        string status,
        int expectedWaiting,
        int expectedActive)
    {
        StatuslineSummaryService service = CreateService();

        await service.ApplyStateAsync(CreateState(status: Enum.Parse<ReviewCycleStatus>(status)));

        JsonNode summary = await ReadSummaryAsync();
        summary["queue"]!["totalWaiting"]!.GetValue<int>().Should().Be(expectedWaiting);
        summary["queue"]!["items"]!.AsArray().Should().HaveCount(expectedWaiting);
        summary["activeReviews"]!.AsArray().Should().HaveCount(expectedActive);
    }

    [Fact]
    public async Task ApplyStateAsync_ShouldWriteQueueAndActiveReviewFields()
    {
        StatuslineSummaryService service = CreateService();

        await service.ApplyStateAsync(CreateState("owner/waiting", 24, reason: "re-review-requested", round: 2));
        await service.ApplyStateAsync(CreateState(
            "owner/running",
            403,
            ReviewCycleStatus.ReviewerRunning,
            round: 3,
            activeRound: 2,
            activeAgent: "claude"));

        JsonNode summary = await ReadSummaryAsync();
        JsonNode item = summary["queue"]!["items"]![0]!;
        item["repository"]!.GetValue<string>().Should().Be("owner/waiting");
        item["prNumber"]!.GetValue<int>().Should().Be(24);
        item["round"]!.GetValue<int>().Should().Be(2);
        item["reason"]!.GetValue<string>().Should().Be("re-review-requested");

        JsonNode active = summary["activeReviews"]![0]!;
        active["repository"]!.GetValue<string>().Should().Be("owner/running");
        active["prNumber"]!.GetValue<int>().Should().Be(403);
        active["round"]!.GetValue<int>().Should().Be(2);
        active["agent"]!.GetValue<string>().Should().Be("claude");
    }

    [Fact]
    public async Task ApplyStateAsync_ShouldIgnoreStateOlderThanCurrent()
    {
        StatuslineSummaryService service = CreateService();
        ReviewCycleState running = CreateState(status: ReviewCycleStatus.ReviewerRunning, updatedAt: _initialTime.AddMinutes(1));

        await service.ApplyStateAsync(running);
        await service.ApplyStateAsync(CreateState(status: ReviewCycleStatus.AwaitingReviewer, updatedAt: _initialTime));

        JsonNode summary = await ReadSummaryAsync();
        summary["queue"]!["totalWaiting"]!.GetValue<int>().Should().Be(0);
        summary["activeReviews"]!.AsArray().Should().ContainSingle();
    }

    [Theory]
    [InlineData("event-last", 0)]
    [InlineData("event-active", 0)]
    [InlineData("event-other", 1)]
    public async Task RemoveEventsAsync_ShouldDropPullRequestOfRemovedEvent(string removedEventId, int expectedActive)
    {
        StatuslineSummaryService service = CreateService();
        await service.ApplyStateAsync(CreateState(
            status: ReviewCycleStatus.ReviewerRunning,
            lastEventId: "event-last",
            activeEventId: "event-active"));

        await service.RemoveEventsAsync([removedEventId]);

        JsonNode summary = await ReadSummaryAsync();
        summary["activeReviews"]!.AsArray().Should().HaveCount(expectedActive);
    }

    // reviewer 実行中に次のイベントが届き、その PR が終了済みとして削除された後で reviewer が終了すると、
    // coordinator は削除済みイベントの待機状態を発行する。これで再登録しない。再オープン後の新しいイベントは反映する
    [Theory]
    [InlineData("event-re-review", 0)]
    [InlineData("event-reopened", 1)]
    public async Task ApplyStateAsync_ShouldNotRestorePullRequestOfRemovedEvent(string lastEventIdAfterRemoval, int expectedWaiting)
    {
        StatuslineSummaryService service = CreateService();
        await service.ApplyStateAsync(CreateState(
            status: ReviewCycleStatus.ReviewerRunning,
            lastEventId: "event-re-review",
            activeEventId: "event-opened"));
        await service.RemoveEventsAsync(["event-opened", "event-re-review"]);

        await service.ApplyStateAsync(CreateState(
            status: ReviewCycleStatus.AwaitingReviewer,
            lastEventId: lastEventIdAfterRemoval,
            updatedAt: _initialTime.AddMinutes(1)));

        JsonNode summary = await ReadSummaryAsync();
        summary["queue"]!["totalWaiting"]!.GetValue<int>().Should().Be(expectedWaiting);
        summary["activeReviews"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task ShutdownAsync_ShouldDeleteSummaryAndIgnoreLaterUpdates()
    {
        StatuslineSummaryService service = CreateService();
        await service.StartAsync();

        await service.ShutdownAsync();
        await service.ApplyStateAsync(CreateState());

        File.Exists(SummaryPath).Should().BeFalse();
    }

    [Fact]
    public async Task ApplyStateAsync_ShouldLogAndKeepPreviousSummary_WhenReplaceIsBlocked()
    {
        StatuslineSummaryService service = CreateService();
        await service.StartAsync();

        using (new FileStream(SummaryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await service.ApplyStateAsync(CreateState());
        }

        (await ReadSummaryAsync())["queue"]!["totalWaiting"]!.GetValue<int>().Should().Be(0);
        string log = await File.ReadAllTextAsync(Path.Combine(LogDirectory, "winui3.log"));
        log.Should().Contain("[Statusline] サマリを書き出せません");

        await service.ApplyStateAsync(CreateState(prNumber: 43));
        (await ReadSummaryAsync())["queue"]!["totalWaiting"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task ReviewCycleStateChanged_ShouldBeReflectedInSummary()
    {
        CreateService();

        await _reviewCycleCoordinator.ObserveEventAsync(CreateReviewEvent());

        JsonNode summary = await WaitForSummaryAsync(node => node["queue"]!["totalWaiting"]!.GetValue<int>() == 1);
        summary["queue"]!["items"]![0]!["reason"]!.GetValue<string>().Should().Be("opened");
    }

    [Fact]
    public async Task EventsRemoved_ShouldDropClosedPullRequestFromSummary()
    {
        CreateService();
        ReviewEvent reviewEvent = CreateReviewEvent();
        await _reviewCycleCoordinator.ObserveEventAsync(reviewEvent);
        await WaitForSummaryAsync(node => node["queue"]!["totalWaiting"]!.GetValue<int>() == 1);
        _cleanupCoordinator.Track(reviewEvent);

        _statusClient.State = PullRequestLifecycleState.Closed;
        await _cleanupCoordinator.RefreshAsync();

        await WaitForSummaryAsync(node => node["queue"]!["totalWaiting"]!.GetValue<int>() == 0);
    }

    private StatuslineSummaryService CreateService()
        => new(_testDirectory, _reviewCycleCoordinator, _cleanupCoordinator, _loggingService, _timeProvider);

    private async Task<JsonNode> ReadSummaryAsync()
        => JsonNode.Parse(await File.ReadAllTextAsync(SummaryPath))!;

    // 状態変化の通知経由の書き出しは待ち合わせる手段が無いため、内容が期待どおりになるまで待つ
    private async Task<JsonNode> WaitForSummaryAsync(Func<JsonNode, bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (true)
        {
            if (File.Exists(SummaryPath))
            {
                JsonNode summary = await ReadSummaryAsync();
                if (predicate(summary))
                {
                    return summary;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }
    }

    private static ReviewCycleState CreateState(
        string repository = "owner/repo",
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

    private static ReviewEvent CreateReviewEvent()
        => new()
        {
            EventId = "event-opened",
            Repository = "owner/repo",
            PrNumber = 42,
            PrUrl = "https://github.com/owner/repo/pull/42",
            Reason = "opened",
            Message = "opened",
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

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
