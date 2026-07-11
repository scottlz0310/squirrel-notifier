// <copyright file="RateLimitGaugeViewModelTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.ViewModels;

namespace SquirrelNotifier.WinUI3.Tests.ViewModels;

public class RateLimitGaugeViewModelTests
{
    private static readonly DateTimeOffset _now = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Update_ShouldPreferHighestUsageLimitForActiveAgent()
    {
        RateLimitGaugeViewModel vm = CreateViewModel();

        vm.Update(
            ["claude-code", "agy"],
            [
                CreateSnapshot("claude-code", CreateLimit("five-hour", "5時間枠", 55), CreateLimit("weekly", "7日枠", 80)),
                CreateSnapshot("agy", CreateLimit("monthly", "月次枠", 95)),
            ],
            "claude-code",
            []);

        vm.SelectedOption!.AgentId.Should().Be("claude-code");
        vm.SelectedOption.LimitId.Should().Be("weekly");
        vm.Severity.Should().Be(RateLimitGaugeSeverity.Warning);
        vm.UsageText.Should().Contain("80%");
    }

    [Theory]
    [InlineData(69.9, "Normal", "状態: 正常")]
    [InlineData(70, "Warning", "状態: 注意")]
    [InlineData(90, "Critical", "状態: 危険")]
    public void Update_ShouldClassifyUsageThresholds(double usedPercentage, string expectedSeverity, string expectedStatus)
    {
        RateLimitGaugeViewModel vm = CreateViewModel();

        vm.Update(
            ["claude-code"],
            [CreateSnapshot("claude-code", CreateLimit("five-hour", "5時間枠", usedPercentage))],
            "claude-code",
            []);

        vm.Severity.Should().Be(Enum.Parse<RateLimitGaugeSeverity>(expectedSeverity));
        vm.StatusText.Should().Be(expectedStatus);
    }

    [Fact]
    public void Update_ShouldKeepManualSelectionWhenSnapshotIsRefreshed()
    {
        RateLimitGaugeViewModel vm = CreateViewModel();
        RateLimitSnapshot initial = CreateSnapshot(
            "claude-code",
            CreateLimit("five-hour", "5時間枠", 80),
            CreateLimit("weekly", "7日枠", 45));
        vm.Update(["claude-code"], [initial], "claude-code", []);
        vm.Select(vm.Options.Single(option => option.LimitId == "weekly"));

        RateLimitSnapshot refreshed = CreateSnapshot(
            "claude-code",
            CreateLimit("five-hour", "5時間枠", 81),
            CreateLimit("weekly", "7日枠", 46));
        vm.Update(["claude-code"], [refreshed], "claude-code", []);

        vm.SelectedOption!.LimitId.Should().Be("weekly");
    }

    [Fact]
    public void Update_ShouldShowUnknownForStaleSnapshot()
    {
        RateLimitGaugeViewModel vm = CreateViewModel();
        RateLimitSnapshot stale = new(
            "claude-code",
            _now.AddMinutes(-16),
            [CreateLimit("five-hour", "5時間枠", 80)]);

        vm.Update(["claude-code"], [stale], "claude-code", []);

        vm.Severity.Should().Be(RateLimitGaugeSeverity.Unknown);
        vm.UsageText.Should().Be("使用率: 取得不可");
        vm.TimingText.Should().Contain("古いデータ");
    }

    [Fact]
    public void Update_ShouldKeepFreshnessWhenUsedPercentageIsMissing()
    {
        RateLimitGaugeViewModel vm = CreateViewModel();
        RateLimitInfo limitWithoutUsage = new()
        {
            Id = "five-hour",
            Label = "5時間枠",
            UsedPercentage = null,
            ResetAt = _now.AddHours(5),
        };

        vm.Update(["claude-code"], [CreateSnapshot("claude-code", limitWithoutUsage)], "claude-code", []);

        vm.Severity.Should().Be(RateLimitGaugeSeverity.Unknown);
        vm.UsageText.Should().Be("使用率: 取得不可");
        vm.TimingText.Should().Contain("fresh").And.NotContain("古いデータ");
    }

    [Fact]
    public void Update_ShouldShowAvailableDeltaForSelectedActiveLimit()
    {
        RateLimitGaugeViewModel vm = CreateViewModel();
        RateLimitDeltaResult delta = new("five-hour", "5時間枠", 12.5, RateLimitDeltaUnavailableReason.None);

        vm.Update(
            ["claude-code"],
            [CreateSnapshot("claude-code", CreateLimit("five-hour", "5時間枠", 80))],
            "claude-code",
            [delta]);

        vm.DeltaText.Should().Be("Delta: +12.5%");
    }

    [Fact]
    public void Update_ShouldShowUnavailableDeltaReason()
    {
        RateLimitGaugeViewModel vm = CreateViewModel();
        RateLimitDeltaResult delta = new("five-hour", "5時間枠", null, RateLimitDeltaUnavailableReason.MissingStartSnapshot);

        vm.Update(
            ["claude-code"],
            [CreateSnapshot("claude-code", CreateLimit("five-hour", "5時間枠", 80))],
            "claude-code",
            [delta]);

        vm.DeltaText.Should().Be("Delta: 取得不可（開始時点の情報がありません）");
    }

    [Fact]
    public void Update_ShouldShowUnavailableWhenSnapshotDoesNotExist()
    {
        RateLimitGaugeViewModel vm = CreateViewModel();

        vm.Update(["claude-code"], [], "claude-code", []);

        vm.SelectedOption!.Severity.Should().Be(RateLimitGaugeSeverity.Unknown);
        vm.TimingText.Should().Be("対応するレートリミット情報がありません");
    }

    private static RateLimitGaugeViewModel CreateViewModel()
        => new(TimeSpan.FromMinutes(15), new FixedTimeProvider(_now));

    private static RateLimitSnapshot CreateSnapshot(string agentId, params RateLimitInfo[] limits)
        => new(agentId, _now, limits);

    private static RateLimitInfo CreateLimit(string id, string label, double usedPercentage)
        => new()
        {
            Id = id,
            Label = label,
            UsedPercentage = usedPercentage,
            ResetAt = _now.AddHours(5),
        };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
