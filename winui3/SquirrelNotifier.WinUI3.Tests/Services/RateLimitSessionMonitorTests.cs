// <copyright file="RateLimitSessionMonitorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class RateLimitSessionMonitorTests : IDisposable
{
    private static readonly DateTimeOffset _now = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
    private readonly string _settingsDirectory = Path.Combine(Path.GetTempPath(), $"RateLimitSessionMonitorTests_{Guid.NewGuid()}");

    [Fact]
    public async Task CaptureEndAsync_ShouldCalculateDeltaForActiveAgent()
    {
        await WriteSnapshotAsync("claude-code", 40);
        RateLimitSessionMonitor monitor = CreateMonitor(["claude-code"], "claude-code");

        IReadOnlyList<RateLimitSnapshot> start = await monitor.CaptureStartAsync(CancellationToken.None);
        await WriteSnapshotAsync("claude-code", 55);
        RateLimitSessionUpdate end = await monitor.CaptureEndAsync(CancellationToken.None);

        start.Should().ContainSingle();
        end.Snapshots.Should().ContainSingle();
        end.Deltas.Should().ContainSingle();
        end.Deltas[0].DeltaPercentage.Should().Be(15);
    }

    [Fact]
    public async Task CaptureStartAsync_ShouldCaptureActiveAgentEvenWhenItIsNotMonitored()
    {
        await WriteSnapshotAsync("claude-code", 40);
        RateLimitSessionMonitor monitor = CreateMonitor([], "claude-code");

        IReadOnlyList<RateLimitSnapshot> snapshots = await monitor.CaptureStartAsync(CancellationToken.None);

        snapshots.Should().ContainSingle(snapshot => snapshot.AgentId == "claude-code");
    }

    [Fact]
    public async Task CaptureStartAsync_ShouldTreatMismatchedAgentIdAsUnavailable()
    {
        await WriteSnapshotAsync("claude-code", 40, payloadAgentId: "agy");
        RateLimitSessionMonitor monitor = CreateMonitor(["claude-code"], "claude-code");

        IReadOnlyList<RateLimitSnapshot> snapshots = await monitor.CaptureStartAsync(CancellationToken.None);

        snapshots.Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_settingsDirectory))
        {
            Directory.Delete(_settingsDirectory, recursive: true);
        }
    }

    private RateLimitSessionMonitor CreateMonitor(IReadOnlyList<string> monitoredAgentIds, string activeAgentId)
    {
        var timeProvider = new FixedTimeProvider(_now);
        return new RateLimitSessionMonitor(
            new RateLimitSnapshotService(new RateLimitFileService(_settingsDirectory)),
            new RateLimitDeltaCalculator(timeProvider),
            monitoredAgentIds,
            activeAgentId,
            TimeSpan.FromMinutes(15),
            timeProvider);
    }

    private async Task WriteSnapshotAsync(string agentId, double usedPercentage, string? payloadAgentId = null)
    {
        string directory = Path.Combine(_settingsDirectory, "ratelimit-status");
        Directory.CreateDirectory(directory);
        string json = $$"""
            {"schemaVersion":1,"agentId":"{{payloadAgentId ?? agentId}}","observedAt":"{{_now:O}}","limits":[{"id":"five-hour","label":"5時間枠","resetAt":"{{_now.AddHours(5):O}}","usedPercentage":{{usedPercentage.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}]}
            """;
        await File.WriteAllTextAsync(Path.Combine(directory, $"{agentId}.json"), json);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
