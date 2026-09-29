// <copyright file="ReviewCiSettleGateTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

// 判定の enum は internal のため、InlineData では名前で受け取り Enum.Parse で解決する
public sealed class ReviewCiSettleGateTests
{
    private const string _shaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string _shaB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static readonly DateTimeOffset _start = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly CiSettleTestClock _clock = new(_start);
    private readonly ScriptedCiSettleSource _source = new();

    // 定数は、待機の部品ではなく、起動前のゲート（呼び出し側）で指定する。CI は最長 8 分の見込み
    [Fact]
    public void WaitOptions_ShouldPollEvery30SecondsAndGiveUpAfter12Minutes()
    {
        ReviewCiSettleGate.WaitOptions.Interval.Should().Be(TimeSpan.FromSeconds(30));
        ReviewCiSettleGate.WaitOptions.MaxWait.Should().Be(TimeSpan.FromMinutes(12));
    }

    [Fact]
    public async Task EvaluateAsync_ShouldHoldAndReserveRecheck_WhenPending()
    {
        _source.Add(ScriptedCiSettleSource.Snapshot("Pending"));
        using ReviewCiSettleGate gate = CreateGate();
        ReviewEvent reviewEvent = CreateReviewEvent();

        CiSettleGateDecision decision = await gate.EvaluateAsync(reviewEvent);

        decision.Action.Should().Be(CiSettleGateAction.Hold);
        decision.HoldReasonText.Should().Be("CI 完了待ち（Pending detail）。確定後に自動起動します");
        gate.IsWaiting(reviewEvent).Should().BeTrue();
        _clock.Timers.Should().ContainSingle().Which.DueTime.Should().Be(ReviewCiSettleGate.WaitOptions.Interval);
        _source.Calls.Should().ContainSingle().Which.Should().Be(("owner/repo", 42, CancellationToken.None));
    }

    [Fact]
    public async Task EvaluateAsync_ShouldRaiseRetryDue_WhenReservationElapses()
    {
        _source.Add(ScriptedCiSettleSource.Snapshot("Pending"));
        using ReviewCiSettleGate gate = CreateGate();
        int raised = 0;
        gate.RetryDue += (_, _) => raised++;
        await gate.EvaluateAsync(CreateReviewEvent());

        _clock.Timers[0].Fire();

        raised.Should().Be(1);
    }

    // 保留のたびに予約されるため、予約は 1 本に保つ（古い予約で二重に再評価しない）
    [Fact]
    public async Task EvaluateAsync_ShouldReplaceReservation_WhenStillPending()
    {
        _source.Add(ScriptedCiSettleSource.Snapshot("Pending"));
        using ReviewCiSettleGate gate = CreateGate();
        ReviewEvent reviewEvent = CreateReviewEvent();
        await gate.EvaluateAsync(reviewEvent);
        _clock.Advance(TimeSpan.FromSeconds(30));

        await gate.EvaluateAsync(reviewEvent);

        _clock.Timers.Should().HaveCount(2);
        _clock.Timers[0].IsDisposed.Should().BeTrue();
        _clock.Timers[1].IsDisposed.Should().BeFalse();
    }

    // 確定・失敗・取得不能は待たずに起動し、PR の close は起動しない。いずれも再確認は予約しない
    [Theory]
    [InlineData("Passed", "Start")]
    [InlineData("Failed", "Start")]
    [InlineData("Unavailable", "Start")]
    [InlineData("PullRequestClosed", "SkipPullRequestClosed")]
    public async Task EvaluateAsync_ShouldNotWaitNorReserve_WhenAlreadyDetermined(string state, string expectedAction)
    {
        _source.Add(ScriptedCiSettleSource.Snapshot(state));
        using ReviewCiSettleGate gate = CreateGate();
        ReviewEvent reviewEvent = CreateReviewEvent();

        CiSettleGateDecision decision = await gate.EvaluateAsync(reviewEvent);

        decision.Action.Should().Be(Enum.Parse<CiSettleGateAction>(expectedAction));
        gate.IsWaiting(reviewEvent).Should().BeFalse();
        _clock.Timers.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateAsync_ShouldStartAndForgetWait_WhenPendingBecomesPassed()
    {
        _source.Add(ScriptedCiSettleSource.Snapshot("Pending"), ScriptedCiSettleSource.Snapshot("Passed"));
        using ReviewCiSettleGate gate = CreateGate();
        ReviewEvent reviewEvent = CreateReviewEvent();
        await gate.EvaluateAsync(reviewEvent);
        _clock.Advance(TimeSpan.FromSeconds(90));

        CiSettleGateDecision decision = await gate.EvaluateAsync(reviewEvent);

        decision.Action.Should().Be(CiSettleGateAction.Start);
        decision.ActivityLog.Should().Contain("CI が確定しました").And.Contain("1 分 30 秒");
        gate.IsWaiting(reviewEvent).Should().BeFalse();
    }

    // 上限に達したら起動する。通知にも注記を残す
    [Fact]
    public async Task EvaluateAsync_ShouldStartWithNote_WhenMaxWaitIsReached()
    {
        _source.Add(ScriptedCiSettleSource.Snapshot("Pending"));
        using ReviewCiSettleGate gate = CreateGate();
        ReviewEvent reviewEvent = CreateReviewEvent();
        CiSettleGateDecision decision = await gate.EvaluateAsync(reviewEvent);
        for (TimeSpan elapsed = TimeSpan.Zero; elapsed < ReviewCiSettleGate.WaitOptions.MaxWait; elapsed += ReviewCiSettleGate.WaitOptions.Interval)
        {
            decision.Action.Should().Be(CiSettleGateAction.Hold);
            _clock.Advance(ReviewCiSettleGate.WaitOptions.Interval);
            decision = await gate.EvaluateAsync(reviewEvent);
        }

        decision.Action.Should().Be(CiSettleGateAction.Start);
        decision.StartNote.Should().Be(ReviewAutoStartPolicy.CiSettleTimedOutText);
        decision.ActivityLog.Should().Contain("CI 待機の上限に達したため起動します").And.Contain("12 分 0 秒");
        gate.IsWaiting(reviewEvent).Should().BeFalse();
    }

    [Fact]
    public async Task EvaluateAsync_ShouldWaitAgainForNewHead_WhenHeadMoves()
    {
        _source.Add(
            ScriptedCiSettleSource.Snapshot("Pending", _shaA),
            ScriptedCiSettleSource.Snapshot("Pending", _shaB));
        using ReviewCiSettleGate gate = CreateGate();
        ReviewEvent reviewEvent = CreateReviewEvent();
        await gate.EvaluateAsync(reviewEvent);
        _clock.Advance(TimeSpan.FromMinutes(11));

        CiSettleGateDecision moved = await gate.EvaluateAsync(reviewEvent);
        _clock.Advance(TimeSpan.FromMinutes(2));
        CiSettleGateDecision afterMove = await gate.EvaluateAsync(reviewEvent);

        moved.Action.Should().Be(CiSettleGateAction.Hold);
        moved.ActivityLog.Should().Contain("head が更新されたため");

        // 最初の head からは 13 分経っているが、新しい head からは 2 分なので待ち続ける
        afterMove.Action.Should().Be(CiSettleGateAction.Hold);
        afterMove.ActivityLog.Should().BeNull();
    }

    [Fact]
    public async Task Forget_ShouldDiscardWait()
    {
        _source.Add(ScriptedCiSettleSource.Snapshot("Pending"));
        using ReviewCiSettleGate gate = CreateGate();
        ReviewEvent reviewEvent = CreateReviewEvent();
        await gate.EvaluateAsync(reviewEvent);

        gate.Forget(reviewEvent);

        gate.IsWaiting(reviewEvent).Should().BeFalse();
    }

    [Fact]
    public async Task EvaluateAsync_ShouldNotReserve_AfterDispose()
    {
        _source.Add(ScriptedCiSettleSource.Snapshot("Pending"));
        ReviewCiSettleGate gate = CreateGate();
        await gate.EvaluateAsync(CreateReviewEvent());

        gate.Dispose();
        await gate.EvaluateAsync(CreateReviewEvent());

        _clock.Timers.Should().ContainSingle().Which.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_ShouldRejectNullEvent()
    {
        using ReviewCiSettleGate gate = CreateGate();

        Func<Task> act = () => gate.EvaluateAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_ShouldRejectNullWaiter()
    {
        Action act = () => _ = new ReviewCiSettleGate(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    private static ReviewEvent CreateReviewEvent() => new()
    {
        Repository = "owner/repo",
        PrNumber = 42,
        Reason = "opened",
    };

    private ReviewCiSettleGate CreateGate() => new(new CiSettleWaiter(_source, _clock), _clock);
}
