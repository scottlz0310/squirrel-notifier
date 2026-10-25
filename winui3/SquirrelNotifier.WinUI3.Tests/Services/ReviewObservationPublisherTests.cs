// <copyright file="ReviewObservationPublisherTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class ReviewObservationPublisherTests : IDisposable
{
    private static readonly DateTimeOffset _now = new(2026, 10, 25, 3, 0, 0, TimeSpan.Zero);
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"ReviewObservationPublisherTests_{Guid.NewGuid():D}");
    private readonly LoggingService _loggingService;
    private readonly RecordingSender _sender = new();
    private readonly List<ReviewStatusEntry> _entries = [];
    private bool _enabled = true;
    private string _producerId = "squirrel-notifier";
    private bool _autoStartEnabled = true;

    public ReviewObservationPublisherTests()
    {
        _loggingService = new LoggingService(LogDirectory);
    }

    private string LogDirectory => Path.Combine(_testDirectory, "logs");

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_ShouldNotSend_WhenDisabled()
    {
        _enabled = false;
        _entries.Add(Waiting("a"));

        await CreatePublisher().RunAsync(isHeartbeat: false);

        _sender.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_ShouldSkipEntriesFromTheLegacyPath()
    {
        _entries.Add(Waiting("a") with { Cycle = null });

        await CreatePublisher().RunAsync(isHeartbeat: false);

        _sender.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_ShouldSendEachStateChangeWithAnIncreasingSequencePerCycle()
    {
        ReviewObservationPublisher publisher = CreatePublisher(epoch: 7);
        _entries.Add(Waiting("a"));
        _entries.Add(Waiting("b"));

        await publisher.RunAsync(isHeartbeat: false);

        _entries[0] = Running("a");
        await publisher.RunAsync(isHeartbeat: false);

        _sender.Sent.Select(static o => (o.CycleId, o.State, o.Sequence)).Should().Equal(
            (CycleId("a"), "waiting", 0L),
            (CycleId("b"), "waiting", 0L),
            (CycleId("a"), "running", 1L));
        _sender.Sent.Should().OnlyContain(static o => o.ProducerId == "squirrel-notifier" && o.ProducerEpoch == 7 && !o.Stopped);
    }

    [Fact]
    public async Task RunAsync_ShouldNotResendAnUnchangedState_ExceptOnHeartbeat()
    {
        ReviewObservationPublisher publisher = CreatePublisher();
        _entries.Add(Running("a"));
        await publisher.RunAsync(isHeartbeat: false);

        await publisher.RunAsync(isHeartbeat: false);
        _sender.Sent.Should().ContainSingle();

        await publisher.RunAsync(isHeartbeat: true);
        _sender.Sent.Select(static o => o.Sequence).Should().Equal(0L, 1L);
        _sender.Sent[1].State.Should().Be("running");
    }

    [Fact]
    public async Task RunAsync_ShouldSendAFinishedEntryOnlyOnce()
    {
        ReviewObservationPublisher publisher = CreatePublisher();
        _entries.Add(Finished("a"));

        await publisher.RunAsync(isHeartbeat: false);
        await publisher.RunAsync(isHeartbeat: true);
        await publisher.RunAsync(isHeartbeat: true);

        _sender.Sent.Should().ContainSingle();
        _sender.Sent[0].Should().Match<ReviewObservation>(static o => o.State == "finished" && o.Outcome == "completed" && o.ExitCode == 0);
    }

    [Fact]
    public async Task RunAsync_ShouldRetryTheLatestStateAfterATransientFailure()
    {
        ReviewObservationPublisher publisher = CreatePublisher();
        _entries.Add(Waiting("a"));
        _sender.Results.Enqueue(ObservationSendResult.Transient("OBSERVATION_COMMUNICATION_FAILED", "down"));
        await publisher.RunAsync(isHeartbeat: false);

        // 失敗している間に状態が進む。送り直すのは、古い状態ではなく、そのときの最新の状態
        _entries[0] = Running("a");
        await publisher.RunAsync(isHeartbeat: false);

        _sender.Sent.Select(static o => (o.State, o.Sequence)).Should().Equal(("waiting", 0L), ("running", 1L));
    }

    [Fact]
    public async Task RunAsync_ShouldRetryAnUnchangedStateAfterATransientFailure_OnTheNextNotification()
    {
        ReviewObservationPublisher publisher = CreatePublisher();
        _entries.Add(Waiting("a"));
        _sender.Results.Enqueue(ObservationSendResult.Transient("OBSERVATION_COMMUNICATION_FAILED", "down"));
        await publisher.RunAsync(isHeartbeat: false);

        await publisher.RunAsync(isHeartbeat: false);
        await publisher.RunAsync(isHeartbeat: false);

        // 受理されるまでは送り直し、受理されたあとは、変化がなければ送らない
        _sender.Sent.Select(static o => o.Sequence).Should().Equal(0L, 1L);
    }

    [Fact]
    public async Task RunAsync_ShouldSendAgainAfterAFailedSend_EvenWhenTheStateReturnsToTheAcknowledgedOne()
    {
        ReviewObservationPublisher publisher = CreatePublisher();
        _entries.Add(Waiting("a"));
        await publisher.RunAsync(isHeartbeat: false);

        // running の送信が失敗する（review-raven が受けたか分からない）。その後、状態は最後に受理された waiting に見える形へ戻る
        _entries[0] = Running("a");
        _sender.Results.Enqueue(ObservationSendResult.Transient("OBSERVATION_COMMUNICATION_FAILED", "down"));
        await publisher.RunAsync(isHeartbeat: false);
        _entries[0] = Waiting("a");
        await publisher.RunAsync(isHeartbeat: false);

        _sender.Sent.Select(static o => (o.State, o.Sequence)).Should().Equal(("waiting", 0L), ("running", 1L), ("waiting", 2L));
    }

    [Fact]
    public async Task RunAsync_ShouldLogARepeatedTransientFailureOnlyOnce()
    {
        ReviewObservationPublisher publisher = CreatePublisher();
        _entries.Add(Waiting("a"));
        for (int i = 0; i < 3; i++)
        {
            _sender.Results.Enqueue(ObservationSendResult.Transient("OBSERVATION_COMMUNICATION_FAILED", "down"));
        }

        await publisher.RunAsync(isHeartbeat: false);
        await publisher.RunAsync(isHeartbeat: true);
        await publisher.RunAsync(isHeartbeat: true);

        _sender.Sent.Should().HaveCount(3);
        (await ReadLogAsync()).Split("OBSERVATION_COMMUNICATION_FAILED").Length.Should().Be(2, "同じ失敗の繰り返しは記録しない");
    }

    [Fact]
    public async Task RunAsync_ShouldStopSendingACycleAfterAPermanentFailure_AndKeepTheOthers()
    {
        ReviewObservationPublisher publisher = CreatePublisher();
        _entries.Add(Waiting("a"));
        _entries.Add(Waiting("b"));
        _sender.Results.Enqueue(ObservationSendResult.Permanent("PRODUCER_NOT_ASSIGNED", "no"));
        await publisher.RunAsync(isHeartbeat: false);

        _entries[0] = Running("a");
        _entries[1] = Running("b");
        await publisher.RunAsync(isHeartbeat: false);
        await publisher.RunAsync(isHeartbeat: true);

        _sender.Sent.Where(static o => o.CycleId == CycleId("a")).Should().ContainSingle("a は割当外と分かったため、送信をやめる");
        _sender.Sent.Where(static o => o.CycleId == CycleId("b")).Select(static o => o.State).Should().Equal("waiting", "running", "running");
        (await ReadLogAsync()).Should().Contain("PRODUCER_NOT_ASSIGNED").And.Contain(CycleId("a"));
    }

    [Fact]
    public async Task RunAsync_ShouldTreatASenderExceptionAsATransientFailure()
    {
        ReviewObservationPublisher publisher = CreatePublisher();
        _entries.Add(Waiting("a"));
        _sender.ThrowNext = new InvalidOperationException("boom");

        Func<Task> act = () => publisher.RunAsync(isHeartbeat: false);

        await act.Should().NotThrowAsync("送信の失敗で、アプリや reviewer の起動を止めない");
        await publisher.RunAsync(isHeartbeat: false);
        _sender.Sent.Select(static o => o.Sequence).Should().Equal(0L, 1L);
    }

    [Fact]
    public async Task RunAsync_ShouldNeverSendTwoObservationsAtTheSameTime_AndSendTheLatestStateAfterwards()
    {
        ReviewObservationPublisher publisher = CreatePublisher();
        _entries.Add(Waiting("a"));
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _sender.Gate = release.Task;

        Task first = publisher.RunAsync(isHeartbeat: false);
        await _sender.WaitForCallAsync();

        // 送信中に状態が進み、通知が 2 回重なる
        _entries[0] = Running("a");
        await publisher.RunAsync(isHeartbeat: false);
        await publisher.RunAsync(isHeartbeat: false);
        _sender.MaxConcurrency.Should().Be(1);

        release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(10));

        _sender.MaxConcurrency.Should().Be(1);
        _sender.Sent.Select(static o => o.State).Should().Equal("waiting", "running");
    }

    [Fact]
    public async Task RunAsync_ShouldReportAnInvalidProducerIdOnce_AndSendNothing()
    {
        _producerId = "Not Valid";
        ReviewObservationPublisher publisher = CreatePublisher();
        _entries.Add(Waiting("a"));

        await publisher.RunAsync(isHeartbeat: false);
        await publisher.RunAsync(isHeartbeat: true);

        _sender.Sent.Should().BeEmpty();
        (await ReadLogAsync()).Split("契約の形式ではない").Length.Should().Be(2);
    }

    [Fact]
    public async Task RunAsync_ShouldUseTheCurrentSettingsOnEveryRun()
    {
        _enabled = false;
        ReviewObservationPublisher publisher = CreatePublisher();
        _entries.Add(Waiting("a"));
        await publisher.RunAsync(isHeartbeat: false);

        _enabled = true;
        await publisher.RunAsync(isHeartbeat: false);

        _sender.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task RunAsync_ShouldDescribeAWaitingEntryWithTheManualHold_WhenAutoStartIsOff()
    {
        _autoStartEnabled = false;
        _entries.Add(Waiting("a"));

        await CreatePublisher().RunAsync(isHeartbeat: false);

        _sender.Sent.Single().HoldReason.Should().Be("manual");
    }

    [Fact]
    public async Task ShutdownAsync_ShouldSendStoppedForUnfinishedEntriesOnly_AndThenSendNothing()
    {
        ReviewObservationPublisher publisher = CreatePublisher();
        _entries.Add(Waiting("a"));
        _entries.Add(Running("b"));
        _entries.Add(Finished("c"));
        await publisher.RunAsync(isHeartbeat: false);
        _sender.Sent.Clear();

        await publisher.ShutdownAsync(TimeSpan.FromSeconds(5));

        _sender.Sent.Select(static o => (o.CycleId, o.State, o.Stopped)).Should().BeEquivalentTo(
            [(CycleId("a"), "waiting", true), (CycleId("b"), "running", true)]);

        _entries[0] = Running("a");
        await publisher.RunAsync(isHeartbeat: false);
        _sender.Sent.Should().HaveCount(2, "終了の通知のあとは送らない");
    }

    [Fact]
    public async Task ShutdownAsync_ShouldNotWaitBeyondTheTimeout_ForASendThatNeverFinishes()
    {
        ReviewObservationPublisher publisher = CreatePublisher();
        _entries.Add(Waiting("a"));
        _sender.Gate = new TaskCompletionSource().Task;
        _ = publisher.RunAsync(isHeartbeat: false);
        await _sender.WaitForCallAsync();

        Task shutdown = publisher.ShutdownAsync(TimeSpan.FromMilliseconds(100));

        (await Task.WhenAny(shutdown, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(shutdown);
    }

    [Fact]
    public async Task OnEntriesChanged_ShouldSendInTheBackground()
    {
        ReviewObservationPublisher publisher = CreatePublisher();
        _entries.Add(Waiting("a"));

        publisher.OnEntriesChanged(this, new ReviewStatusChangedEventArgs(IsHeartbeat: false));

        await _sender.WaitForCallAsync();
        _sender.Sent.Should().ContainSingle();
    }

    private static string CycleId(string name) => $"cyc_{name}{new string('0', 25)}";

    private static ReviewCycleRef Cycle(string name)
        => new($"cand_{name}{new string('0', 25)}", CycleId(name), 1, new string('a', 40));

    private static ReviewStatusEntry Waiting(string name)
        => new($"owner/repo#{name}", "owner/repo", 1, 1, $"evt_{name}", "opened", ReviewStatusState.Waiting, _now.AddMinutes(-5), Cycle: Cycle(name));

    private static ReviewStatusEntry Running(string name)
        => Waiting(name) with { State = ReviewStatusState.Running, StartedAt = _now.AddMinutes(-1) };

    private static ReviewStatusEntry Finished(string name)
        => Running(name) with { State = ReviewStatusState.Finished, FinishedAt = _now, Outcome = ReviewerOutcome.Completed, ExitCode = 0 };

    private ReviewObservationPublisher CreatePublisher(long epoch = 1)
        => new(
            () => Task.FromResult<IReadOnlyList<ReviewStatusEntry>>([.. _entries]),
            _sender,
            () => new ReviewObservationPublisherOptions(_enabled, _producerId),
            () => _autoStartEnabled,
            epoch,
            _loggingService,
            new FixedTimeProvider(_now));

    // LoggingService.WriteAsync は、書き込みを待ってから戻る
    private Task<string> ReadLogAsync()
        => Task.FromResult(Directory.Exists(LogDirectory)
            ? string.Concat(Directory.GetFiles(LogDirectory).Select(File.ReadAllText))
            : string.Empty);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class RecordingSender : IObservationSender
    {
        private readonly object _lock = new();
        private readonly TaskCompletionSource _called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _concurrent;

        public List<ReviewObservation> Sent { get; } = [];

        public Queue<ObservationSendResult> Results { get; } = new();

        public Exception? ThrowNext { get; set; }

        public Task? Gate { get; set; }

        public int MaxConcurrency { get; private set; }

        // 回帰したときに、テストが永久に待たず、失敗するようにする
        public Task WaitForCallAsync() => _called.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public async Task<ObservationSendResult> SendAsync(ReviewObservation observation, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                MaxConcurrency = Math.Max(MaxConcurrency, ++_concurrent);
                Sent.Add(observation);
            }

            _called.TrySetResult();
            try
            {
                if (Gate is not null)
                {
                    await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                if (ThrowNext is { } exception)
                {
                    ThrowNext = null;
                    throw exception;
                }

                return Results.Count > 0 ? Results.Dequeue() : ObservationSendResult.Accepted;
            }
            finally
            {
                lock (_lock)
                {
                    _concurrent--;
                }
            }
        }
    }
}
