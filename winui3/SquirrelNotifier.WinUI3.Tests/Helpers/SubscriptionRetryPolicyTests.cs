// <copyright file="SubscriptionRetryPolicyTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public class SubscriptionRetryPolicyTests
{
    private const string _connRefused = SubscriptionRetryPolicy.DependencyNotReadyErrorTag;

    [Theory]
    // 予算内は固定 5 秒間隔で待ち続ける。指数バックオフの回数上限には影響されない
    [InlineData(1, 0)]
    [InlineData(2, 5000)]
    [InlineData(20, 100000)]
    [InlineData(60, 299999)]
    public void Decide_DependencyNotReady_WithinBudget_ShouldWaitAtFixedInterval(
        int consecutiveFailureCount,
        long elapsedMs)
    {
        SubscriptionRetryDecision decision = SubscriptionRetryPolicy.Decide(
            _connRefused,
            consecutiveFailureCount,
            elapsedMs,
            maxRetries: 5);

        decision.ShouldRetry.Should().BeTrue();
        decision.DelayMs.Should().Be(SubscriptionRetryPolicy.DependencyWaitIntervalMs);
        decision.IsWaitingForDependency.Should().BeTrue();
    }

    [Theory]
    [InlineData(300000)]
    [InlineData(300001)]
    [InlineData(600000)]
    public void Decide_DependencyNotReady_BudgetExceeded_ShouldGiveUp(long elapsedMs)
    {
        SubscriptionRetryDecision decision = SubscriptionRetryPolicy.Decide(
            _connRefused,
            consecutiveFailureCount: 60,
            elapsedMs,
            maxRetries: 5);

        decision.ShouldRetry.Should().BeFalse();
        decision.IsWaitingForDependency.Should().BeTrue();
    }

    [Theory]
    [InlineData("[AUTH_REQUIRED]")]
    [InlineData("[HTTP_404]")]
    [InlineData("[AUTH_ERROR]")]
    [InlineData("[GENERAL_ERROR]")]
    [InlineData("[UNKNOWN_ERROR]")]
    public void Decide_OtherErrors_ShouldNotWaitForDependency(string errorTag)
    {
        // 待っても解決しないエラーは経過時間に関係なく従来どおり回数で打ち切る
        SubscriptionRetryPolicy.Decide(errorTag, 1, 0, maxRetries: 5)
            .IsWaitingForDependency.Should().BeFalse();
        SubscriptionRetryPolicy.Decide(errorTag, 6, 0, maxRetries: 5)
            .ShouldRetry.Should().BeFalse();
    }

    [Theory]
    // 従来の指数バックオフ（1s → 2s → 4s → 8s → 16s、上限 32s）と一致すること
    [InlineData(1, 1000)]
    [InlineData(2, 2000)]
    [InlineData(3, 4000)]
    [InlineData(4, 8000)]
    [InlineData(5, 16000)]
    [InlineData(6, 32000)]
    [InlineData(7, 32000)]
    [InlineData(100, 32000)]
    public void Decide_GeneralError_ShouldUseExponentialBackoff(int consecutiveFailureCount, int expectedDelayMs)
    {
        SubscriptionRetryDecision decision = SubscriptionRetryPolicy.Decide(
            "[GENERAL_ERROR]",
            consecutiveFailureCount,
            consecutiveFailureElapsedMs: 0,
            maxRetries: 1000);

        decision.ShouldRetry.Should().BeTrue();
        decision.DelayMs.Should().Be(expectedDelayMs);
    }

    [Theory]
    [InlineData(0, 1, false)]
    [InlineData(1, 1, true)]
    [InlineData(1, 2, false)]
    [InlineData(5, 5, true)]
    [InlineData(5, 6, false)]
    public void Decide_GeneralError_ShouldRespectMaxRetries(
        int maxRetries,
        int consecutiveFailureCount,
        bool expectedShouldRetry)
    {
        SubscriptionRetryPolicy.Decide("[GENERAL_ERROR]", consecutiveFailureCount, 0, maxRetries)
            .ShouldRetry.Should().Be(expectedShouldRetry);
    }

    [Fact]
    public void Decide_DependencyNotReady_WithZeroBudget_ShouldGiveUpImmediately()
    {
        SubscriptionRetryPolicy.Decide(_connRefused, 1, 0, maxRetries: 5, dependencyWaitBudgetMs: 0)
            .ShouldRetry.Should().BeFalse();
    }

    [Theory]
    [InlineData("[AUTH_REQUIRED]", false)]
    [InlineData("[CONN_REFUSED]", true)]
    [InlineData("[GENERAL_ERROR]", true)]
    [InlineData("[HTTP_404]", true)]
    [InlineData("[AUTH_ERROR]", true)]
    [InlineData("[TLS_CERT_UNTRUSTED]", true)]
    [InlineData("[DNS_LOOKUP_FAILED]", true)]
    [InlineData("[UNKNOWN_ERROR]", true)]
    public void ShouldAutoRecover_ShouldExcludeOnlyAuthenticationRequired(string errorTag, bool expected)
    {
        SubscriptionRetryPolicy.ShouldAutoRecover(errorTag).Should().Be(expected);
    }

    [Theory]
    // 初回 60 秒から指数バックオフ（60s → 120s → 240s）で増加し、最大 5 分（300s）で頭打ちになること
    [InlineData(0, 60000)]
    [InlineData(1, 60000)]
    [InlineData(2, 120000)]
    [InlineData(3, 240000)]
    [InlineData(4, 300000)]
    [InlineData(5, 300000)]
    [InlineData(100, 300000)]
    public void CalculateRecoveryBackoffMs_ShouldUseExponentialBackoffWithFiveMinuteCap(
        int recoveryAttempt,
        int expectedDelayMs)
    {
        SubscriptionRetryPolicy.CalculateRecoveryBackoffMs(recoveryAttempt)
            .Should().Be(expectedDelayMs);
    }
}
