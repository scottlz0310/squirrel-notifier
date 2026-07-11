// <copyright file="AutoPauseGateTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class AutoPauseGateTests
{
    private static readonly DateTimeOffset _now = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan _freshness = TimeSpan.FromMinutes(15);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Evaluate_ShouldReturnNotApplicableWhenAgentIdIsMissing(string? agentId)
    {
        AutoPauseGate gate = CreateGate();

        AutoPauseDecision decision = gate.Evaluate(agentId, [CreateSnapshot("claude-code", _now, 99)], _freshness);

        decision.Status.Should().Be(AutoPauseStatus.NotApplicable);
        decision.PausedLimit.Should().BeNull();
    }

    [Theory]
    [InlineData(94.9, "Allowed")]
    [InlineData(95, "Paused")]
    [InlineData(100, "Paused")]
    public void Evaluate_ShouldClassifyPauseThreshold(double usedPercentage, string expectedStatus)
    {
        AutoPauseGate gate = CreateGate();

        AutoPauseDecision decision = gate.Evaluate("claude-code", [CreateSnapshot("claude-code", _now, usedPercentage)], _freshness);

        decision.Status.Should().Be(Enum.Parse<AutoPauseStatus>(expectedStatus));
    }

    [Fact]
    public void Evaluate_ShouldPauseWhenAnyLimitReachesThreshold()
    {
        AutoPauseGate gate = CreateGate();
        RateLimitSnapshot snapshot = new(
            "agy",
            _now,
            [
                CreateLimit("session", "セッション枠", 20),
                CreateLimit("weekly", "7日枠", 96),
                CreateLimit("monthly", "月次枠", 50),
            ]);

        AutoPauseDecision decision = gate.Evaluate("agy", [snapshot], _freshness);

        decision.Status.Should().Be(AutoPauseStatus.Paused);
        decision.PausedLimit!.LimitId.Should().Be("weekly");
        decision.PausedLimit.UsedPercentage.Should().Be(96);
    }

    [Fact]
    public void Evaluate_ShouldNotPauseUnpausedAgentOnStaleOrMissingData()
    {
        AutoPauseGate gate = CreateGate();
        RateLimitSnapshot stale = CreateSnapshot("claude-code", _now.AddMinutes(-16), 99);

        gate.Evaluate("claude-code", [stale], _freshness).Status.Should().Be(AutoPauseStatus.Allowed);
        gate.Evaluate("claude-code", [], _freshness).Status.Should().Be(AutoPauseStatus.Allowed);
    }

    [Fact]
    public void Evaluate_ShouldKeepPausedOnStaleSnapshotEvenAfterResetAtHasPassed()
    {
        AutoPauseGate gate = CreateGate();
        gate.Evaluate("claude-code", [CreateSnapshot("claude-code", _now, 97)], _freshness);

        // resetAt を過去に設定した stale snapshot。resetAt 通過だけを根拠に解除しない
        RateLimitSnapshot stale = new(
            "claude-code",
            _now.AddMinutes(-16),
            [CreateLimit("five-hour", "5時間枠", 97, resetAt: _now.AddMinutes(-5))]);

        AutoPauseDecision decision = gate.Evaluate("claude-code", [stale], _freshness);

        decision.Status.Should().Be(AutoPauseStatus.Paused);
        decision.PausedLimit!.UsedPercentage.Should().Be(97);
    }

    [Fact]
    public void Evaluate_ShouldKeepPausedWhenSnapshotIsMissing()
    {
        AutoPauseGate gate = CreateGate();
        gate.Evaluate("claude-code", [CreateSnapshot("claude-code", _now, 97)], _freshness);

        AutoPauseDecision decision = gate.Evaluate("claude-code", [], _freshness);

        decision.Status.Should().Be(AutoPauseStatus.Paused);
    }

    [Fact]
    public void Evaluate_ShouldKeepPausedWhenFreshSnapshotHasNoUsedPercentage()
    {
        AutoPauseGate gate = CreateGate();
        gate.Evaluate("claude-code", [CreateSnapshot("claude-code", _now, 97)], _freshness);
        RateLimitSnapshot noUsage = new(
            "claude-code",
            _now,
            [CreateLimit("five-hour", "5時間枠", usedPercentage: null)]);

        AutoPauseDecision decision = gate.Evaluate("claude-code", [noUsage], _freshness);

        decision.Status.Should().Be(AutoPauseStatus.Paused);
    }

    [Fact]
    public void Evaluate_ShouldResumeOnFreshRecoverySnapshot()
    {
        AutoPauseGate gate = CreateGate();
        gate.Evaluate("claude-code", [CreateSnapshot("claude-code", _now, 97)], _freshness);

        AutoPauseDecision decision = gate.Evaluate("claude-code", [CreateSnapshot("claude-code", _now, 10)], _freshness);

        decision.Status.Should().Be(AutoPauseStatus.Allowed);
        gate.PausedLimits.Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_ShouldTrackPausedStatePerAgent()
    {
        AutoPauseGate gate = CreateGate();
        gate.Evaluate("claude-code", [CreateSnapshot("claude-code", _now, 97)], _freshness);

        AutoPauseDecision agyDecision = gate.Evaluate("agy", [CreateSnapshot("agy", _now, 10)], _freshness);

        agyDecision.Status.Should().Be(AutoPauseStatus.Allowed);
        gate.PausedLimits.Should().ContainSingle(paused => paused.AgentId == "claude-code");
    }

    [Fact]
    public void Evaluate_ShouldRaiseStateChangedOnPauseAndResume()
    {
        AutoPauseGate gate = CreateGate();
        int raised = 0;
        gate.StateChanged += (_, _) => raised++;

        gate.Evaluate("claude-code", [CreateSnapshot("claude-code", _now, 97)], _freshness);
        raised.Should().Be(1);

        // 同一根拠の再評価では発火しない
        gate.Evaluate("claude-code", [CreateSnapshot("claude-code", _now, 97)], _freshness);
        raised.Should().Be(1);

        gate.Evaluate("claude-code", [CreateSnapshot("claude-code", _now, 10)], _freshness);
        raised.Should().Be(2);
    }

    [Fact]
    public void BuildReasonText_ShouldContainLimitAndUsage()
    {
        AutoPausedLimit paused = new("claude-code", "five-hour", "5時間枠", 96.5, _now.AddHours(2), _now);

        string text = paused.BuildReasonText();

        text.Should().Contain("5時間枠").And.Contain("96.5%");
    }

    private static AutoPauseGate CreateGate() => new(new FixedTimeProvider(_now));

    private static RateLimitSnapshot CreateSnapshot(string agentId, DateTimeOffset observedAt, double usedPercentage)
        => new(agentId, observedAt, [CreateLimit("five-hour", "5時間枠", usedPercentage)]);

    private static RateLimitInfo CreateLimit(string id, string label, double? usedPercentage, DateTimeOffset? resetAt = null)
        => new()
        {
            Id = id,
            Label = label,
            UsedPercentage = usedPercentage,
            ResetAt = resetAt ?? _now.AddHours(5),
        };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
