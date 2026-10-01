// <copyright file="ReviewStatusPipelineTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Globalization;
using System.Text.Json.Nodes;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

// 受信 → 起動待ち → 保留 → 起動 → 終了の流れを、実際のコーディネーターをつないで通し、公開ファイル review-status.json に出る内容を検証する（#462）。
// 各部品の単体テスト（ReviewStartCoordinatorTests・ReviewStatusServiceTests）では、つなぎ目（保留の観測が公開ファイルへ届くこと）を固定できないため、
// 実機では再現しにくい保留の種類（manual / autoPause / ciPending）も含めて、ここで固定する。
public sealed class ReviewStatusPipelineTests : IDisposable
{
    private const string _pausedAgentId = "claude-code";
    private static readonly TimeSpan _waitTimeout = TimeSpan.FromSeconds(5);

    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ReviewStatusPipelineTests_{Guid.NewGuid():D}");
    private readonly PendingReviewStartQueue _pendingQueue = new();
    private readonly AutoPauseResumeScheduler _resumeScheduler = new();
    private readonly CiSettleTestClock _ciClock = new(new DateTimeOffset(2026, 10, 1, 1, 0, 0, TimeSpan.Zero));
    private readonly ScriptedCiSettleSource _ciSource = new();
    private readonly FakeLauncherService _launcher = new();
    private readonly SettingsService _settings;
    private readonly LoggingService _logging;
    private readonly ReviewEventCleanupCoordinator _cleanup;
    private readonly ReviewCycleCoordinator _cycle;
    private readonly ReviewStatusService _statusService;
    private readonly ReviewCiSettleGate _ciGate;
    private int _eventSequence;

    public ReviewStatusPipelineTests()
    {
        _settings = new SettingsService(_testDirectory, pnpmBinDir: string.Empty);
        _logging = new LoggingService(Path.Combine(_testDirectory, "logs"));
        _cleanup = new ReviewEventCleanupCoordinator(new OpenStatusClient(), _logging);
        _cycle = new ReviewCycleCoordinator(new ReviewCycleStore(Path.Combine(_testDirectory, "cycles")), _logging);
        _ciGate = new ReviewCiSettleGate(new CiSettleWaiter(_ciSource, _ciClock), _ciClock);
        _statusService = new ReviewStatusService(
            _testDirectory,
            _cycle,
            _cleanup,
            _logging,
            "0.0.0-test",
            () => _settings.Settings.AutoReviewStartEnabled,
            () => SubscriptionState.Running,
            () => false);
    }

    private string StatusPath => Path.Combine(_testDirectory, ReviewStatusService.FileName);

    public void Dispose()
    {
        _statusService.ShutdownAsync().GetAwaiter().GetResult();
        _cleanup.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _ciGate.Dispose();
        _resumeScheduler.Dispose();
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsync_ShouldPublishEmptyDocument_AndShutdownAsync_ShouldDeleteIt()
    {
        await _statusService.StartAsync();

        JsonNode status = ReadStatus();
        status["items"]!.AsArray().Should().BeEmpty();
        status["recent"]!.AsArray().Should().BeEmpty();
        status["concurrency"]!["active"]!.GetValue<int>().Should().Be(0);

        await _statusService.ShutdownAsync();

        File.Exists(StatusPath).Should().BeFalse("終了時は、ファイル不在 = 未起動を表すため削除する");
        File.Exists(StatusPath + ".tmp").Should().BeFalse();
    }

    // 保留の種類ごとに、受信から保留までの実際の流れを通し、保留の理由と待ち順が公開ファイルへ届くことを確かめる
    [Theory]
    [InlineData("manual", "manual", false)]
    [InlineData("busy", "busy", true)]
    [InlineData("autoPause", "autoPause", true)]
    [InlineData("ciPending", "ciPending", true)]
    public async Task ProcessAsync_ShouldPublishHoldReason_WhenReviewIsHeld(
        string scenario,
        string expectedHoldReason,
        bool expectsQueuePosition)
    {
        await _statusService.StartAsync();
        ReviewEventProcessingCoordinator processing = await ArrangeAsync(scenario);
        ReviewEvent reviewEvent = CreateReviewEvent("owner/Repo", 42);

        await processing.ProcessAsync(reviewEvent);

        JsonNode status = await WaitForStatusAsync(
            document => document["items"]!.AsArray().Count == 1
                && document["items"]![0]!["holdReason"] is not null);
        JsonNode item = status["items"]![0]!;
        item["key"]!.GetValue<string>().Should().Be("owner/repo#42");
        item["repository"]!.GetValue<string>().Should().Be("owner/Repo", "repository は元の表記のまま出す");
        item["state"]!.GetValue<string>().Should().Be("waiting");
        item["holdReason"]!.GetValue<string>().Should().Be(expectedHoldReason);
        item["holdSince"].Should().NotBeNull();
        item["eventId"]!.GetValue<string>().Should().Be(reviewEvent.EventId);
        if (expectsQueuePosition)
        {
            item["queuePosition"]!.GetValue<int>().Should().Be(1);
        }
        else
        {
            item["queuePosition"].Should().BeNull("manual は保留の待ち順を持たない（利用者の操作を待つ）");
        }

        status["concurrency"]!["active"]!.GetValue<int>().Should().Be(0);
        _launcher.StartedSessions.Should().BeEmpty("保留している間は、reviewer を起動しない");
    }

    // 実行中のレビューがあるとき、保留した順に待ち順を付ける
    [Fact]
    public async Task ProcessAsync_ShouldNumberQueuePositionsInHoldOrder_WhenSeveralReviewsAreHeldBehindARunningReview()
    {
        await _statusService.StartAsync();
        ReviewEventProcessingCoordinator processing = await ArrangeAsync("busy");

        await processing.ProcessAsync(CreateReviewEvent("owner/first", 1));
        await processing.ProcessAsync(CreateReviewEvent("owner/second", 2));

        JsonNode status = await WaitForStatusAsync(
            document => document["items"]!.AsArray().Count == 2
                && document["items"]!.AsArray().All(item => item!["queuePosition"] is not null));
        JsonArray items = status["items"]!.AsArray();
        items.Select(item => (item!["key"]!.GetValue<string>(), item["queuePosition"]!.GetValue<int>()))
            .Should().BeEquivalentTo(new[] { ("owner/first#1", 1), ("owner/second#2", 2) });
        items.Should().OnlyContain(item => item!["holdReason"]!.GetValue<string>() == "busy");
    }

    // 受信 → 起動待ち → 実行中 → 終了。終了は、プロセスの結果（Verdict ではない）として recent[] へ移る
    [Theory]
    [InlineData(true, "completed")]
    [InlineData(false, "failed")]
    public async Task ProcessAsync_ShouldFollowRunningThenFinished_WhenReviewStartsAutomatically(
        bool success,
        string expectedOutcome)
    {
        await _statusService.StartAsync();
        ReviewEventProcessingCoordinator processing = await ArrangeAsync("idle");
        ReviewEvent reviewEvent = CreateReviewEvent("owner/Repo", 42);

        await processing.ProcessAsync(reviewEvent);

        JsonNode running = await WaitForStatusAsync(
            document => document["items"]!.AsArray().Count == 1
                && document["items"]![0]!["state"]!.GetValue<string>() == "running");
        JsonNode runningItem = running["items"]![0]!;
        runningItem["agent"]!.GetValue<string>().Should().Be(_settings.Settings.ReviewerLauncherPresetId);
        runningItem["startedAt"].Should().NotBeNull();
        runningItem["holdReason"].Should().BeNull("実行中の項目は、保留の理由を持たない");
        running["concurrency"]!["active"]!.GetValue<int>().Should().Be(1);

        _launcher.StartedSessions.Should().ContainSingle().Which.Complete(
            success ? AgentExecutionOutcome.Succeeded : AgentExecutionOutcome.Failed,
            new LauncherResult { Success = success });

        JsonNode finished = await WaitForStatusAsync(document => document["recent"]!.AsArray().Count == 1);
        finished["items"]!.AsArray().Should().BeEmpty();
        finished["concurrency"]!["active"]!.GetValue<int>().Should().Be(0);
        JsonNode recent = finished["recent"]![0]!;
        recent["key"]!.GetValue<string>().Should().Be("owner/repo#42");
        recent["state"]!.GetValue<string>().Should().Be("finished");
        recent["outcome"]!.GetValue<string>().Should().Be(expectedOutcome);
        recent["receivedAt"].Should().NotBeNull();
        recent["startedAt"].Should().NotBeNull();
        recent["finishedAt"].Should().NotBeNull();
    }

    // 保留の解除（実行中のレビューの終了）で、保留した項目が起動して running になり、保留の理由が消える
    [Fact]
    public async Task ProcessPendingAsync_ShouldStartHeldReview_AndClearHoldReason_WhenRunningReviewEnds()
    {
        await _statusService.StartAsync();
        ReviewEventProcessingCoordinator processing = await ArrangeAsync("busy");
        await processing.ProcessAsync(CreateReviewEvent("owner/Repo", 42));
        await WaitForStatusAsync(document => document["items"]![0]!["holdReason"] is not null);

        _launcher.IsRunning = false;
        await processing.ProcessPendingAsync();

        JsonNode status = await WaitForStatusAsync(
            document => document["items"]!.AsArray().Count == 1
                && document["items"]![0]!["state"]!.GetValue<string>() == "running");
        JsonNode item = status["items"]![0]!;
        item["holdReason"].Should().BeNull();
        item["queuePosition"].Should().BeNull();
        status["concurrency"]!["active"]!.GetValue<int>().Should().Be(1);
    }

    // シナリオに合わせて、設定・起動中の状態・Auto-Pause・CI を整え、受信イベントを処理するコーディネーターを返す
    private async Task<ReviewEventProcessingCoordinator> ArrangeAsync(string scenario)
    {
        bool autoStart = scenario != "manual";
        _settings.UpdateAutoReviewStartEnabled(autoStart);
        ReviewCiSettleGate? ciGate = null;
        switch (scenario)
        {
            case "busy":
                _launcher.IsRunning = true;
                break;
            case "autoPause":
                await WriteSnapshotAsync(_pausedAgentId, usedPercentage: 96);
                break;
            case "ciPending":
                _ciSource.Add(ScriptedCiSettleSource.Snapshot("Pending"));
                ciGate = _ciGate;
                break;
        }

        ReviewStartCoordinator start = new(
            _launcher,
            _settings,
            new RateLimitSnapshotService(new RateLimitFileService(_testDirectory)),
            new AutoPauseGate(),
            _pendingQueue,
            _resumeScheduler,
            _logging,
            _cycle,
            ciGate);
        return new ReviewEventProcessingCoordinator(
            new ReviewEventCollectionCoordinator(),
            _cleanup,
            _pendingQueue,
            _logging,
            start.TryStartAutomaticallyAsync,
            _cycle,
            start.IsWaitingForCiSettle);
    }

    private async Task WriteSnapshotAsync(string agentId, double usedPercentage)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string directory = Path.Combine(_testDirectory, "ratelimit-status");
        Directory.CreateDirectory(directory);
        string json = $$"""
            {"schemaVersion":1,"agentId":"{{agentId}}","observedAt":"{{now:O}}","limits":[{"id":"five-hour","label":"5時間枠","resetAt":"{{now.AddHours(5):O}}","usedPercentage":{{usedPercentage.ToString(CultureInfo.InvariantCulture)}}}]}
            """;
        await File.WriteAllTextAsync(Path.Combine(directory, $"{agentId}.json"), json);
    }

    private ReviewEvent CreateReviewEvent(string repository, int prNumber)
        => new()
        {
            EventId = $"event-{Interlocked.Increment(ref _eventSequence)}",
            Repository = repository,
            PrNumber = prNumber,
            PrUrl = $"https://github.com/{repository}/pull/{prNumber}",
            Reason = "opened",
            Message = "opened",
            ReceivedTime = DateTime.UtcNow,
        };

    private JsonNode ReadStatus() => JsonNode.Parse(File.ReadAllText(StatusPath))!;

    // 公開ファイルは状態の変化の後に非同期で書き出されるため、条件を満たすまで待つ
    private async Task<JsonNode> WaitForStatusAsync(Func<JsonNode, bool> predicate)
    {
        DateTime deadline = DateTime.UtcNow + _waitTimeout;
        JsonNode? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                last = ReadStatus();
                if (predicate(last))
                {
                    return last;
                }
            }
            catch (IOException)
            {
                // 置換の瞬間は、読めないことがある。次の周期で読み直す
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"review-status.json が、条件を満たしませんでした: {last?.ToJsonString()}");
    }

    private sealed class OpenStatusClient : IPullRequestStatusClient
    {
        public Task<PullRequestLifecycleState> GetStateAsync(string repository, int prNumber, CancellationToken cancellationToken)
            => Task.FromResult(PullRequestLifecycleState.Open);
    }

    private sealed class FakeLauncherService : IReviewLauncherService
    {
        public event EventHandler? RunCompleted
        {
            add { }
            remove { }
        }

        public bool IsRunning { get; set; }

        public List<AgentExecutionSession> StartedSessions { get; } = [];

        public AgentExecutionSession StartSession(ReviewEvent reviewEvent, LauncherRole role, CancellationToken cancellationToken)
        {
            AgentExecutionSession session = new(TimeProvider.System);
            StartedSessions.Add(session);
            return session;
        }

        public Task<LauncherResult> LaunchAsync(ReviewEvent reviewEvent, LauncherRole role, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public void Cancel() => throw new NotSupportedException();

        public Task<string> BuildCommandLineAsync(ReviewEvent reviewEvent, LauncherRole role, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
