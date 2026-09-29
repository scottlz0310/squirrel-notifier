// <copyright file="ReviewAutoStartPolicyTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

// 判定対象の enum は internal のため、InlineData では名前で受け取り Enum.Parse で解決する
public sealed class ReviewAutoStartPolicyTests
{
    [Theory]
    [InlineData(true, "opened", false, "Start")]
    [InlineData(true, "synchronized", false, "Start")]
    [InlineData(true, "re-review-requested", false, "Start")]
    [InlineData(false, "opened", false, "SkippedDisabled")]
    [InlineData(false, "opened", true, "SkippedDisabled")]
    [InlineData(false, "review-posted", false, "SkippedDisabled")]
    [InlineData(true, "review-posted", false, "SkippedUnsupportedReason")]
    [InlineData(true, "", false, "SkippedUnsupportedReason")]
    [InlineData(true, "review-posted", true, "SkippedUnsupportedReason")]
    [InlineData(true, "opened", true, "SkippedBusy")]
    [InlineData(true, "re-review-requested", true, "SkippedBusy")]
    public void Evaluate_ShouldDecideAutoStart(bool autoStartEnabled, string reason, bool isReviewBusy, string expectedOutcome)
    {
        ReviewAutoStartOutcome result = ReviewAutoStartPolicy.Evaluate(autoStartEnabled, reason, isReviewBusy);

        result.Should().Be(Enum.Parse<ReviewAutoStartOutcome>(expectedOutcome));
    }

    [Theory]
    [InlineData("Manual", true)]
    [InlineData("Automatic", false)]
    public void AllowsAutoPauseOverridePrompt_ShouldOnlyPromptForManualStart(string trigger, bool expected)
    {
        bool result = ReviewAutoStartPolicy.AllowsAutoPauseOverridePrompt(Enum.Parse<ReviewStartTrigger>(trigger));

        result.Should().Be(expected);
    }

    [Fact]
    public void DescribeSkipReason_ShouldReturnReason_WhenReasonIsUnsupported()
    {
        ReviewAutoStartPolicy.DescribeSkipReason(ReviewAutoStartOutcome.SkippedUnsupportedReason)
            .Should().NotBeNullOrWhiteSpace();
    }

    // 設定 off のときに行を残すと、off の挙動が現行と変わってしまうため記録しない。
    // 実行中による見送りは保留として別に記録する（#339）
    [Theory]
    [InlineData("Start")]
    [InlineData("SkippedDisabled")]
    [InlineData("SkippedBusy")]
    public void DescribeSkipReason_ShouldReturnNull_WhenNothingToRecord(string outcome)
    {
        ReviewAutoStartPolicy.DescribeSkipReason(Enum.Parse<ReviewAutoStartOutcome>(outcome))
            .Should().BeNull();
    }

    // 保留理由のラベルは通知に出る（#456）
    [Fact]
    public void CiSettleHoldLabel_ShouldBeCiCompletionWait()
    {
        ReviewAutoStartPolicy.CiSettleHoldLabel.Should().Be("CI 完了待ち");
    }

    // CI の確定待ちは最適化であり安全性の gate ではない。起動しないのは PR が閉じている場合だけ（#456）
    [Theory]
    [InlineData("Waiting", false, 0, "Hold", null, "CI 完了待ち（未完了: build）。確定後に自動起動します", null)]
    [InlineData("Waiting", true, 0, "Hold", "head が更新されたため、新しい head の CI を待ち直します（未完了: build）。", "CI 完了待ち（未完了: build）。確定後に自動起動します", null)]
    [InlineData("Settled", false, 0, "Start", null, null, null)]
    [InlineData("Settled", false, 200, "Start", "CI が確定しました（未完了: build。待機 3 分 20 秒）。", null, null)]
    [InlineData("Settled", false, 45, "Start", "CI が確定しました（未完了: build。待機 45 秒）。", null, null)]
    [InlineData("Failed", false, 90, "Start", "CI に失敗があるため、待たずに起動します（未完了: build）。", null, null)]
    [InlineData("TimedOut", false, 720, "Start", "CI 待機の上限に達したため起動します（未完了: build。待機 12 分 0 秒）。", null, "CI 待機の上限に達したため起動")]
    [InlineData("Unavailable", false, 0, "Start", "CI の状態を取得できないため、待たずに起動します: 未完了: build", null, null)]
    [InlineData("PullRequestClosed", false, 0, "SkipPullRequestClosed", "PR が merge または close されているため、自動起動しません。", null, null)]
    public void DecideCiSettle_ShouldMapWaitResultToAutoStartDecision(
        string outcome,
        bool headMoved,
        int waitedSeconds,
        string expectedAction,
        string? expectedActivityLog,
        string? expectedHoldReasonText,
        string? expectedStartNote)
    {
        CiSettleWaitResult result = new(
            Enum.Parse<CiSettleWaitOutcome>(outcome),
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "未完了: build",
            TimeSpan.FromSeconds(waitedSeconds),
            TimeSpan.FromSeconds(30),
            headMoved);

        CiSettleGateDecision decision = ReviewAutoStartPolicy.DecideCiSettle(result);

        decision.Action.Should().Be(Enum.Parse<CiSettleGateAction>(expectedAction));
        decision.ActivityLog.Should().Be(expectedActivityLog);
        decision.HoldReasonText.Should().Be(expectedHoldReasonText);
        decision.StartNote.Should().Be(expectedStartNote);
    }

    [Fact]
    public void DecideCiSettle_ShouldRejectNullAndUnknownOutcome()
    {
        Action nullResult = () => ReviewAutoStartPolicy.DecideCiSettle(null!);
        Action unknownOutcome = () => ReviewAutoStartPolicy.DecideCiSettle(
            new CiSettleWaitResult((CiSettleWaitOutcome)99, null, string.Empty, TimeSpan.Zero));

        nullResult.Should().Throw<ArgumentNullException>();
        unknownOutcome.Should().Throw<ArgumentOutOfRangeException>();
    }
}
