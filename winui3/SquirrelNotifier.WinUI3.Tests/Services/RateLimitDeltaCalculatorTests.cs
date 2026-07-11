// <copyright file="RateLimitDeltaCalculatorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Collections.Generic;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class RateLimitDeltaCalculatorTests
{
    private static readonly DateTimeOffset _now = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan _threshold = TimeSpan.FromMinutes(15);

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static RateLimitDeltaCalculator CreateCalculator() => new(new FixedTimeProvider());

    private static RateLimitInfo CreateLimit(string id, string label, DateTimeOffset resetAt, double? usedPercentage)
        => new() { Id = id, Label = label, ResetAt = resetAt, UsedPercentage = usedPercentage };

    private static RateLimitSnapshot CreateSnapshot(DateTimeOffset observedAt, params RateLimitInfo[] limits)
        => new("claude-code", observedAt, limits);

    [Fact]
    public void Compute_BothSnapshotsMissing_ShouldReturnEmpty()
    {
        RateLimitDeltaCalculator calculator = CreateCalculator();

        IReadOnlyList<RateLimitDeltaResult> results = calculator.Compute(null, null, _now, _threshold);

        results.Should().BeEmpty();
    }

    [Fact]
    public void Compute_EndSnapshotMissing_ShouldReturnMissingEndSnapshotForStartLimits()
    {
        RateLimitSnapshot start = CreateSnapshot(_now, CreateLimit("5h", "5時間枠", _now.AddHours(5), 50));
        RateLimitDeltaCalculator calculator = CreateCalculator();

        IReadOnlyList<RateLimitDeltaResult> results = calculator.Compute(start, null, _now, _threshold);

        results.Should().ContainSingle();
        results[0].IsAvailable.Should().BeFalse();
        results[0].UnavailableReason.Should().Be(RateLimitDeltaUnavailableReason.MissingEndSnapshot);
        results[0].DeltaPercentage.Should().BeNull();
    }

    [Fact]
    public void Compute_StartSnapshotMissing_ShouldReturnMissingStartSnapshotForEndLimits()
    {
        RateLimitSnapshot end = CreateSnapshot(_now, CreateLimit("5h", "5時間枠", _now.AddHours(5), 70));
        RateLimitDeltaCalculator calculator = CreateCalculator();

        IReadOnlyList<RateLimitDeltaResult> results = calculator.Compute(null, end, _now, _threshold);

        results.Should().ContainSingle();
        results[0].UnavailableReason.Should().Be(RateLimitDeltaUnavailableReason.MissingStartSnapshot);
    }

    [Fact]
    public void Compute_StartSnapshotOlderThanThresholdRelativeToNow_ButFreshAtCaptureTime_ShouldReturnDelta()
    {
        // レビュー対応: 既定 launcher timeout（30分）相当の長時間レビューでも、開始スナップショットが
        // 「開始しようとした時刻」において fresh であれば Delta を算出できることを固定する。
        // 「now」基準で判定すると、レビューサイクルの長さそのもので常に stale 判定されてしまっていた
        DateTimeOffset sessionStart = _now - TimeSpan.FromMinutes(30);
        DateTimeOffset resetAt = _now.AddHours(5);
        RateLimitSnapshot start = CreateSnapshot(sessionStart, CreateLimit("5h", "5時間枠", resetAt, 50));
        RateLimitSnapshot end = CreateSnapshot(_now, CreateLimit("5h", "5時間枠", resetAt, 73));
        RateLimitDeltaCalculator calculator = CreateCalculator();

        IReadOnlyList<RateLimitDeltaResult> results = calculator.Compute(start, end, sessionStart, _threshold);

        results.Should().ContainSingle();
        results[0].IsAvailable.Should().BeTrue();
        results[0].DeltaPercentage.Should().Be(23);
    }

    [Fact]
    public void Compute_StartSnapshotStale_ShouldReturnStartSnapshotStale()
    {
        // セッション開始しようとした時点で、既に取得済みの snapshot 自体が古かったケース
        // （statusline が長時間発火していなかった等）
        DateTimeOffset sessionStart = _now - TimeSpan.FromMinutes(5);
        RateLimitSnapshot start = CreateSnapshot(sessionStart - TimeSpan.FromMinutes(20), CreateLimit("5h", "5時間枠", _now.AddHours(5), 50));
        RateLimitSnapshot end = CreateSnapshot(_now, CreateLimit("5h", "5時間枠", _now.AddHours(5), 70));
        RateLimitDeltaCalculator calculator = CreateCalculator();

        IReadOnlyList<RateLimitDeltaResult> results = calculator.Compute(start, end, sessionStart, _threshold);

        results.Should().ContainSingle();
        results[0].UnavailableReason.Should().Be(RateLimitDeltaUnavailableReason.StartSnapshotStale);
    }

    [Fact]
    public void Compute_EndSnapshotStale_ShouldReturnEndSnapshotStale()
    {
        DateTimeOffset sessionStart = _now - TimeSpan.FromMinutes(5);
        RateLimitSnapshot start = CreateSnapshot(sessionStart, CreateLimit("5h", "5時間枠", _now.AddHours(5), 50));
        RateLimitSnapshot end = CreateSnapshot(_now - TimeSpan.FromMinutes(20), CreateLimit("5h", "5時間枠", _now.AddHours(5), 70));
        RateLimitDeltaCalculator calculator = CreateCalculator();

        IReadOnlyList<RateLimitDeltaResult> results = calculator.Compute(start, end, sessionStart, _threshold);

        results.Should().ContainSingle();
        results[0].UnavailableReason.Should().Be(RateLimitDeltaUnavailableReason.EndSnapshotStale);
    }

    [Fact]
    public void Compute_ValidFreshSnapshots_ShouldReturnDelta()
    {
        DateTimeOffset sessionStart = _now - TimeSpan.FromMinutes(10);
        DateTimeOffset resetAt = _now.AddHours(5);
        RateLimitSnapshot start = CreateSnapshot(sessionStart, CreateLimit("5h", "5時間枠", resetAt, 50));
        RateLimitSnapshot end = CreateSnapshot(_now, CreateLimit("5h", "5時間枠", resetAt, 73));
        RateLimitDeltaCalculator calculator = CreateCalculator();

        IReadOnlyList<RateLimitDeltaResult> results = calculator.Compute(start, end, sessionStart, _threshold);

        results.Should().ContainSingle();
        results[0].IsAvailable.Should().BeTrue();
        results[0].DeltaPercentage.Should().Be(23);
    }

    [Fact]
    public void Compute_ResetBoundaryCrossed_ShouldNotComputeDelta()
    {
        // resetAt が変化している = 開始・終了の間でリセットが発生している
        DateTimeOffset sessionStart = _now - TimeSpan.FromMinutes(10);
        RateLimitSnapshot start = CreateSnapshot(sessionStart, CreateLimit("5h", "5時間枠", _now.AddMinutes(5), 95));
        RateLimitSnapshot end = CreateSnapshot(_now, CreateLimit("5h", "5時間枠", _now.AddHours(5), 10));
        RateLimitDeltaCalculator calculator = CreateCalculator();

        IReadOnlyList<RateLimitDeltaResult> results = calculator.Compute(start, end, sessionStart, _threshold);

        results.Should().ContainSingle();
        results[0].UnavailableReason.Should().Be(RateLimitDeltaUnavailableReason.ResetBoundaryCrossed);
        results[0].DeltaPercentage.Should().BeNull();
    }

    [Fact]
    public void Compute_LimitMissingInStart_ShouldReturnLimitMissingInStart()
    {
        DateTimeOffset sessionStart = _now - TimeSpan.FromMinutes(10);
        DateTimeOffset resetAt = _now.AddHours(5);
        RateLimitSnapshot start = CreateSnapshot(sessionStart, CreateLimit("7d", "週次枠", resetAt, 20));
        RateLimitSnapshot end = CreateSnapshot(_now, CreateLimit("5h", "5時間枠", resetAt, 30));
        RateLimitDeltaCalculator calculator = CreateCalculator();

        IReadOnlyList<RateLimitDeltaResult> results = calculator.Compute(start, end, sessionStart, _threshold);

        results.Should().ContainSingle();
        results[0].LimitId.Should().Be("5h");
        results[0].UnavailableReason.Should().Be(RateLimitDeltaUnavailableReason.LimitMissingInStart);
    }

    [Theory]
    [InlineData(null, 30.0)]
    [InlineData(30.0, null)]
    [InlineData(null, null)]
    public void Compute_UsedPercentageMissing_ShouldReturnUsedPercentageMissing(double? startPct, double? endPct)
    {
        DateTimeOffset sessionStart = _now - TimeSpan.FromMinutes(10);
        DateTimeOffset resetAt = _now.AddHours(5);
        RateLimitSnapshot start = CreateSnapshot(sessionStart, CreateLimit("5h", "5時間枠", resetAt, startPct));
        RateLimitSnapshot end = CreateSnapshot(_now, CreateLimit("5h", "5時間枠", resetAt, endPct));
        RateLimitDeltaCalculator calculator = CreateCalculator();

        IReadOnlyList<RateLimitDeltaResult> results = calculator.Compute(start, end, sessionStart, _threshold);

        results.Should().ContainSingle();
        results[0].UnavailableReason.Should().Be(RateLimitDeltaUnavailableReason.UsedPercentageMissing);
    }

    [Fact]
    public void Compute_MultipleLimits_ShouldEvaluateEachIndependently()
    {
        DateTimeOffset sessionStart = _now - TimeSpan.FromMinutes(10);
        DateTimeOffset resetAt = _now.AddHours(5);
        RateLimitSnapshot start = CreateSnapshot(
            sessionStart,
            CreateLimit("5h", "5時間枠", resetAt, 50),
            CreateLimit("7d", "週次枠", resetAt, null));
        RateLimitSnapshot end = CreateSnapshot(
            _now,
            CreateLimit("5h", "5時間枠", resetAt, 60),
            CreateLimit("7d", "週次枠", resetAt, 40));
        RateLimitDeltaCalculator calculator = CreateCalculator();

        IReadOnlyList<RateLimitDeltaResult> results = calculator.Compute(start, end, sessionStart, _threshold);

        results.Should().HaveCount(2);
        results.Should().ContainSingle(r => r.LimitId == "5h" && r.DeltaPercentage == 10);
        results.Should().ContainSingle(r => r.LimitId == "7d" && r.UnavailableReason == RateLimitDeltaUnavailableReason.UsedPercentageMissing);
    }

    [Fact]
    public void Compute_DuplicateIdInStartSnapshot_ShouldNotThrow_AndReturnDuplicateLimitId()
    {
        // レビュー対応: 例外を出さないだけでなく、どちらの値が正しいか判断できない以上
        // 数値を返してはならない（先頭採用による恣意的な値の表示を防ぐ）
        DateTimeOffset sessionStart = _now - TimeSpan.FromMinutes(10);
        DateTimeOffset resetAt = _now.AddHours(5);
        RateLimitSnapshot start = CreateSnapshot(
            sessionStart,
            CreateLimit("5h", "5時間枠(1)", resetAt, 40),
            CreateLimit("5h", "5時間枠(2・重複ID)", resetAt, 90));
        RateLimitSnapshot end = CreateSnapshot(_now, CreateLimit("5h", "5時間枠", resetAt, 70));
        RateLimitDeltaCalculator calculator = CreateCalculator();

        Action act = () => calculator.Compute(start, end, sessionStart, _threshold);

        act.Should().NotThrow();

        IReadOnlyList<RateLimitDeltaResult> results = calculator.Compute(start, end, sessionStart, _threshold);
        results.Should().ContainSingle();
        results[0].IsAvailable.Should().BeFalse();
        results[0].UnavailableReason.Should().Be(RateLimitDeltaUnavailableReason.DuplicateLimitId);
        results[0].DeltaPercentage.Should().BeNull();
    }

    [Fact]
    public void Compute_DuplicateIdInEndSnapshot_ShouldNotThrow_AndReturnDuplicateLimitId()
    {
        // 終了 snapshot 側の重複も同様に扱う（レビュー対応）
        DateTimeOffset sessionStart = _now - TimeSpan.FromMinutes(10);
        DateTimeOffset resetAt = _now.AddHours(5);
        RateLimitSnapshot start = CreateSnapshot(sessionStart, CreateLimit("5h", "5時間枠", resetAt, 40));
        RateLimitSnapshot end = CreateSnapshot(
            _now,
            CreateLimit("5h", "5時間枠(1)", resetAt, 70),
            CreateLimit("5h", "5時間枠(2・重複ID)", resetAt, 20));
        RateLimitDeltaCalculator calculator = CreateCalculator();

        Action act = () => calculator.Compute(start, end, sessionStart, _threshold);

        act.Should().NotThrow();

        IReadOnlyList<RateLimitDeltaResult> results = calculator.Compute(start, end, sessionStart, _threshold);
        results.Should().ContainSingle();
        results[0].UnavailableReason.Should().Be(RateLimitDeltaUnavailableReason.DuplicateLimitId);
        results[0].DeltaPercentage.Should().BeNull();
    }
}
