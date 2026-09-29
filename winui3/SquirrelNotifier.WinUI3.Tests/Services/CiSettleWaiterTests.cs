// <copyright file="CiSettleWaiterTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

// 状態の enum は internal のため、InlineData では名前で受け取り Enum.Parse で解決する
public sealed class CiSettleWaiterTests
{
    private const string _repository = "owner/repo";
    private const string _shaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string _shaB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static readonly CiSettleWaitOptions _options = new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(12));
    private static readonly DateTimeOffset _start = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly TestTimeProvider _clock = new(_start);
    private readonly ScriptedSource _source = new();

    [Theory]
    [InlineData("Passed", "Settled")]
    [InlineData("Failed", "Failed")]
    [InlineData("Unavailable", "Unavailable")]
    [InlineData("PullRequestClosed", "PullRequestClosed")]
    public async Task CheckAsync_ShouldReturnTerminalOutcome_WithoutWaiting_WhenAlreadyDetermined(string state, string expectedOutcome)
    {
        _source.Add(Snapshot(state));

        CiSettleWaitResult result = await CreateWaiter().CheckAsync(_repository, 42, _options, CancellationToken.None);

        result.Outcome.Should().Be(Enum.Parse<CiSettleWaitOutcome>(expectedOutcome));
        result.IsTerminal.Should().BeTrue();
        result.Waited.Should().Be(TimeSpan.Zero);
        result.Detail.Should().Be($"{state} detail");
        result.HeadSha.Should().Be(state == "Unavailable" ? null : _shaA);
    }

    [Fact]
    public async Task CheckAsync_ShouldReturnWaitingWithRetryInterval_WhenPending()
    {
        _source.Add(Snapshot("Pending"));

        CiSettleWaitResult result = await CreateWaiter().CheckAsync(_repository, 42, _options, CancellationToken.None);

        result.Outcome.Should().Be(CiSettleWaitOutcome.Waiting);
        result.IsTerminal.Should().BeFalse();
        result.RetryAfter.Should().Be(_options.Interval);
        result.Waited.Should().Be(TimeSpan.Zero);
        result.HeadMoved.Should().BeFalse();
        result.HeadSha.Should().Be(_shaA);
    }

    // 上限は、最初に未確定を観測した時点からの経過で数える。確認は間隔ごとに続いている状況にする
    [Theory]
    [InlineData(30, "Waiting")]
    [InlineData(690, "Waiting")]
    [InlineData(720, "TimedOut")]
    public async Task CheckAsync_ShouldTimeOut_OnlyWhenPendingReachesMaxWait(int elapsedSeconds, string expectedOutcome)
    {
        _source.Add(Snapshot("Pending"));
        CiSettleWaiter waiter = CreateWaiter();
        CiSettleWaitResult result = await waiter.CheckAsync(_repository, 42, _options, CancellationToken.None);

        for (int seconds = 30; seconds <= elapsedSeconds; seconds += 30)
        {
            _clock.Advance(TimeSpan.FromSeconds(30));
            result = await waiter.CheckAsync(_repository, 42, _options, CancellationToken.None);
        }

        result.Outcome.Should().Be(Enum.Parse<CiSettleWaitOutcome>(expectedOutcome));
        result.Waited.Should().Be(TimeSpan.FromSeconds(elapsedSeconds));
    }

    // 確認の間隔に収まらない経過でも、上限を超えた最初の確認で打ち切る
    [Fact]
    public async Task CheckAsync_ShouldTimeOut_OnFirstCheckAfterMaxWait_WhenIntervalDoesNotDivideMaxWait()
    {
        _source.Add(Snapshot("Pending"));
        CiSettleWaiter waiter = CreateWaiter();
        await waiter.CheckAsync(_repository, 42, _options, CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(690));
        await waiter.CheckAsync(_repository, 42, _options, CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(45));

        CiSettleWaitResult result = await waiter.CheckAsync(_repository, 42, _options, CancellationToken.None);

        result.Outcome.Should().Be(CiSettleWaitOutcome.TimedOut);
        result.Waited.Should().Be(TimeSpan.FromSeconds(735));
    }

    [Fact]
    public async Task CheckAsync_ShouldReturnSettledWithWaitedTime_WhenPendingBecomesPassed()
    {
        _source.Add(Snapshot("Pending"), Snapshot("Pending"), Snapshot("Passed"));
        CiSettleWaiter waiter = CreateWaiter();
        await waiter.CheckAsync(_repository, 42, _options, CancellationToken.None);
        _clock.Advance(_options.Interval);
        await waiter.CheckAsync(_repository, 42, _options, CancellationToken.None);
        _clock.Advance(_options.Interval);

        CiSettleWaitResult result = await waiter.CheckAsync(_repository, 42, _options, CancellationToken.None);

        result.Outcome.Should().Be(CiSettleWaitOutcome.Settled);
        result.Waited.Should().Be(TimeSpan.FromSeconds(60));
    }

    // 失敗は、未確定のまま待たずに返す（失敗はレビューの入力になる）
    [Fact]
    public async Task CheckAsync_ShouldReturnFailedWithWaitedTime_WhenPendingBecomesFailed()
    {
        _source.Add(Snapshot("Pending"), Snapshot("Failed"));
        CiSettleWaiter waiter = CreateWaiter();
        await waiter.CheckAsync(_repository, 42, _options, CancellationToken.None);
        _clock.Advance(_options.Interval);

        CiSettleWaitResult result = await waiter.CheckAsync(_repository, 42, _options, CancellationToken.None);

        result.Outcome.Should().Be(CiSettleWaitOutcome.Failed);
        result.Waited.Should().Be(_options.Interval);
    }

    // 終端に達したら待機の状態を破棄する。次の未確定は新しい待機として数える
    [Theory]
    [InlineData("Passed")]
    [InlineData("Failed")]
    [InlineData("Unavailable")]
    [InlineData("PullRequestClosed")]
    public async Task CheckAsync_ShouldStartNewWait_AfterTerminalOutcome(string terminalState)
    {
        _source.Add(Snapshot("Pending"), Snapshot(terminalState), Snapshot("Pending"));
        CiSettleWaiter waiter = CreateWaiter();
        await waiter.CheckAsync(_repository, 42, _options, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await waiter.CheckAsync(_repository, 42, _options, CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(30));

        CiSettleWaitResult result = await waiter.CheckAsync(_repository, 42, _options, CancellationToken.None);

        result.Outcome.Should().Be(CiSettleWaitOutcome.Waiting);
        result.Waited.Should().Be(TimeSpan.Zero);
    }

    // head が動いたら、新しい head に対して待ち直す（開始時刻と上限を数え直す）
    [Fact]
    public async Task CheckAsync_ShouldRestartWait_WhenHeadMoves()
    {
        _source.Add(Snapshot("Pending", _shaA), Snapshot("Pending", _shaB), Snapshot("Pending", _shaB), Snapshot("Pending", _shaB));
        CiSettleWaiter waiter = CreateWaiter();
        await waiter.CheckAsync(_repository, 42, _options, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(10));
        CiSettleWaitResult moved = await waiter.CheckAsync(_repository, 42, _options, CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(5));
        CiSettleWaitResult afterMove = await waiter.CheckAsync(_repository, 42, _options, CancellationToken.None);

        moved.Outcome.Should().Be(CiSettleWaitOutcome.Waiting);
        moved.HeadMoved.Should().BeTrue();
        moved.Waited.Should().Be(TimeSpan.Zero);
        moved.HeadSha.Should().Be(_shaB);

        // 最初の head からは 15 分経っているが、新しい head からは 5 分なので上限に達しない
        afterMove.Outcome.Should().Be(CiSettleWaitOutcome.Waiting);
        afterMove.HeadMoved.Should().BeFalse();
        afterMove.Waited.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task CheckAsync_ShouldNotReportHeadMoved_WhenHeadIsUnchanged()
    {
        _source.Add(Snapshot("Pending", _shaA));
        CiSettleWaiter waiter = CreateWaiter();
        await waiter.CheckAsync(_repository, 42, _options, CancellationToken.None);
        _clock.Advance(_options.Interval);

        CiSettleWaitResult result = await waiter.CheckAsync(_repository, 42, _options, CancellationToken.None);

        result.HeadMoved.Should().BeFalse();
        result.Waited.Should().Be(_options.Interval);
    }

    // 確認が上限以上途切れていたら、連続した待機ではないため、上限に達したとせず待ち直す
    [Fact]
    public async Task CheckAsync_ShouldStartNewWait_WhenChecksWereInterruptedForMaxWait()
    {
        _source.Add(Snapshot("Pending"));
        CiSettleWaiter waiter = CreateWaiter();
        await waiter.CheckAsync(_repository, 42, _options, CancellationToken.None);
        _clock.Advance(_options.MaxWait);

        CiSettleWaitResult result = await waiter.CheckAsync(_repository, 42, _options, CancellationToken.None);

        result.Outcome.Should().Be(CiSettleWaitOutcome.Waiting);
        result.Waited.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task CheckAsync_ShouldNotReportStaleWaitedTime_WhenTerminalAfterInterruption()
    {
        _source.Add(Snapshot("Pending"), Snapshot("Passed"));
        CiSettleWaiter waiter = CreateWaiter();
        await waiter.CheckAsync(_repository, 42, _options, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(30));

        CiSettleWaitResult result = await waiter.CheckAsync(_repository, 42, _options, CancellationToken.None);

        result.Outcome.Should().Be(CiSettleWaitOutcome.Settled);
        result.Waited.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task CheckAsync_ShouldTrackEachPullRequestIndependently_AndIgnoreRepositoryCase()
    {
        _source.Add(Snapshot("Pending"));
        CiSettleWaiter waiter = CreateWaiter();
        await waiter.CheckAsync("Owner/Repo", 42, _options, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(1));

        CiSettleWaitResult sameRepositoryOtherCase = await waiter.CheckAsync("owner/repo", 42, _options, CancellationToken.None);
        CiSettleWaitResult otherPullRequest = await waiter.CheckAsync("owner/repo", 43, _options, CancellationToken.None);

        sameRepositoryOtherCase.Waited.Should().Be(TimeSpan.FromMinutes(1));
        otherPullRequest.Waited.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task Forget_ShouldDiscardWaitState()
    {
        _source.Add(Snapshot("Pending"));
        CiSettleWaiter waiter = CreateWaiter();
        await waiter.CheckAsync(_repository, 42, _options, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));

        waiter.Forget(_repository, 42);
        CiSettleWaitResult result = await waiter.CheckAsync(_repository, 42, _options, CancellationToken.None);

        result.Waited.Should().Be(TimeSpan.Zero);
    }

    // 待機中は、未確定を観測してから終端・破棄までの間だけ
    [Theory]
    [InlineData("Pending", true)]
    [InlineData("Passed", false)]
    [InlineData("Failed", false)]
    public async Task IsWaiting_ShouldBeTrue_OnlyWhilePending(string lastState, bool expectedWaiting)
    {
        _source.Add(Snapshot("Pending"), Snapshot(lastState));
        CiSettleWaiter waiter = CreateWaiter();
        waiter.IsWaiting(_repository, 42).Should().BeFalse();

        await waiter.CheckAsync(_repository, 42, _options, CancellationToken.None);
        waiter.IsWaiting("OWNER/REPO", 42).Should().BeTrue();
        await waiter.CheckAsync(_repository, 42, _options, CancellationToken.None);

        waiter.IsWaiting(_repository, 42).Should().Be(expectedWaiting);
        waiter.IsWaiting(_repository, 43).Should().BeFalse();
    }

    [Fact]
    public async Task IsWaiting_ShouldBeFalse_AfterForget()
    {
        _source.Add(Snapshot("Pending"));
        CiSettleWaiter waiter = CreateWaiter();
        await waiter.CheckAsync(_repository, 42, _options, CancellationToken.None);

        waiter.Forget(_repository, 42);

        waiter.IsWaiting(_repository, 42).Should().BeFalse();
    }

    [Fact]
    public async Task CheckAsync_ShouldPassArgumentsAndTokenToSource()
    {
        _source.Add(Snapshot("Passed"));
        using CancellationTokenSource cts = new();

        await CreateWaiter().CheckAsync(_repository, 42, _options, cts.Token);

        _source.Calls.Should().ContainSingle().Which.Should().Be((_repository, 42, cts.Token));
    }

    // 取得元が契約に反して例外を送出しても、待てないだけで先へ進める（fail-open）。原因は Detail に残す
    [Theory]
    [InlineData("InvalidOperationException", "壊れた応答")]
    [InlineData("HttpRequestException", "接続できません")]
    public async Task CheckAsync_ShouldReturnUnavailableWithCause_WhenSourceThrows(string exceptionName, string message)
    {
        _source.Add(exceptionName == nameof(HttpRequestException)
            ? new HttpRequestException(message)
            : new InvalidOperationException(message));

        CiSettleWaitResult result = await CreateWaiter().CheckAsync(_repository, 42, _options, CancellationToken.None);

        result.Outcome.Should().Be(CiSettleWaitOutcome.Unavailable);
        result.Detail.Should().Contain(exceptionName).And.Contain(message);
    }

    // 取得元の内部タイムアウトによるキャンセルは、呼び出し側のキャンセルではないため取得不能として扱う
    [Fact]
    public async Task CheckAsync_ShouldReturnUnavailable_WhenSourceIsCancelledInternally()
    {
        _source.Add(new OperationCanceledException("内部のタイムアウト"));

        CiSettleWaitResult result = await CreateWaiter().CheckAsync(_repository, 42, _options, CancellationToken.None);

        result.Outcome.Should().Be(CiSettleWaitOutcome.Unavailable);
    }

    [Fact]
    public async Task CheckAsync_ShouldPropagateCancellation_WhenCallerCancels()
    {
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        _source.Add(new OperationCanceledException(cts.Token));

        Func<Task> act = () => CreateWaiter().CheckAsync(_repository, 42, _options, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(0, 720)]
    [InlineData(-30, 720)]
    [InlineData(30, 0)]
    [InlineData(30, -720)]
    public async Task CheckAsync_ShouldRejectNonPositiveOptions(int intervalSeconds, int maxWaitSeconds)
    {
        CiSettleWaitOptions options = new(TimeSpan.FromSeconds(intervalSeconds), TimeSpan.FromSeconds(maxWaitSeconds));

        Func<Task> act = () => CreateWaiter().CheckAsync(_repository, 42, options, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        _source.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task CheckAsync_ShouldRejectBlankRepository(string repository)
    {
        Func<Task> act = () => CreateWaiter().CheckAsync(repository, 42, _options, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // 終端で 1 回だけ待つ用途。確認の間隔ごとに時間を進め、確定したら返す
    [Fact]
    public async Task WaitAsync_ShouldPollAtIntervalUntilSettled()
    {
        _clock.FireTimersImmediately = true;
        _source.Add(Snapshot("Pending"), Snapshot("Pending"), Snapshot("Passed"));

        CiSettleWaitResult result = await CreateWaiter().WaitAsync(_repository, 42, _options, CancellationToken.None);

        result.Outcome.Should().Be(CiSettleWaitOutcome.Settled);
        result.Waited.Should().Be(_options.Interval * 2);
        _source.Calls.Should().HaveCount(3);
    }

    [Fact]
    public async Task WaitAsync_ShouldReturnTimedOut_WhenNeverSettles()
    {
        _clock.FireTimersImmediately = true;
        _source.Add(Snapshot("Pending"));

        CiSettleWaitResult result = await CreateWaiter().WaitAsync(_repository, 42, _options, CancellationToken.None);

        result.Outcome.Should().Be(CiSettleWaitOutcome.TimedOut);
        result.Waited.Should().Be(_options.MaxWait);
        _source.Calls.Should().HaveCount((int)(_options.MaxWait / _options.Interval) + 1);
    }

    // 上限は呼び出し側が引数で決める。終端の待機では起動前のゲートと別の値にできる
    [Theory]
    [InlineData(60, 3)]
    [InlineData(300, 11)]
    public async Task WaitAsync_ShouldHonorMaxWaitGivenByCaller(int maxWaitSeconds, int expectedChecks)
    {
        _clock.FireTimersImmediately = true;
        _source.Add(Snapshot("Pending"));
        CiSettleWaitOptions options = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(maxWaitSeconds));

        CiSettleWaitResult result = await CreateWaiter().WaitAsync(_repository, 42, options, CancellationToken.None);

        result.Outcome.Should().Be(CiSettleWaitOutcome.TimedOut);
        _source.Calls.Should().HaveCount(expectedChecks);
    }

    [Fact]
    public async Task WaitAsync_ShouldRestartWait_WhenHeadMovesWhileWaiting()
    {
        _clock.FireTimersImmediately = true;
        _source.Add(Snapshot("Pending", _shaA), Snapshot("Pending", _shaB), Snapshot("Passed", _shaB));

        CiSettleWaitResult result = await CreateWaiter().WaitAsync(_repository, 42, _options, CancellationToken.None);

        result.Outcome.Should().Be(CiSettleWaitOutcome.Settled);
        result.HeadSha.Should().Be(_shaB);
        result.Waited.Should().Be(_options.Interval);
    }

    // キャンセルされたら待機の状態を破棄する。次の待機は新しく数える
    [Fact]
    public async Task WaitAsync_ShouldForgetWaitState_WhenCancelled()
    {
        _source.Add(Snapshot("Pending"));
        CiSettleWaiter waiter = CreateWaiter();
        await waiter.CheckAsync(_repository, 42, _options, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));
        using CancellationTokenSource cts = new();

        Task<CiSettleWaitResult> wait = waiter.WaitAsync(_repository, 42, _options, cts.Token);
        await cts.CancelAsync();
        await wait.Awaiting(task => task).Should().ThrowAsync<OperationCanceledException>();
        CiSettleWaitResult afterCancel = await waiter.CheckAsync(_repository, 42, _options, CancellationToken.None);

        afterCancel.Waited.Should().Be(TimeSpan.Zero);
    }

    private static CiSettleSnapshot Snapshot(string state, string sha = _shaA)
        => new(Enum.Parse<CiSettleState>(state), state == "Unavailable" ? null : sha, $"{state} detail");

    private CiSettleWaiter CreateWaiter() => new(_source, _clock);

    // 記録した順に返し、尽きたら最後の 1 件を返し続ける
    private sealed class ScriptedSource : ICiSettleSource
    {
        private readonly List<object> _script = [];
        private int _index;

        public List<(string Repository, int PrNumber, CancellationToken Token)> Calls { get; } = [];

        public void Add(params object[] items) => _script.AddRange(items);

        public Task<CiSettleSnapshot> GetAsync(string repository, int prNumber, CancellationToken cancellationToken)
        {
            Calls.Add((repository, prNumber, cancellationToken));
            object item = _script[Math.Min(_index, _script.Count - 1)];
            _index++;
            return item switch
            {
                Exception exception => throw exception,
                CiSettleSnapshot snapshot => Task.FromResult(snapshot),
                _ => throw new InvalidOperationException("スクリプトの要素が不正です。"),
            };
        }
    }

    // 時刻を手動で進める。FireTimersImmediately の間は、待機（Task.Delay）を要求された分だけ時刻を進めて即座に満了させる
    private sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public bool FireTimersImmediately { get; set; }

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (FireTimersImmediately && dueTime > TimeSpan.Zero)
            {
                Advance(dueTime);

                // Task.Delay が ITimer を受け取る前に完了させないため、コールバックは ThreadPool へ回す
                ThreadPool.QueueUserWorkItem(_ => callback(state));
            }

            return new NoopTimer();
        }

        private sealed class NoopTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
