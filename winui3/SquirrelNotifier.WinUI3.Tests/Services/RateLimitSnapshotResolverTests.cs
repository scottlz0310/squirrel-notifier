// <copyright file="RateLimitSnapshotResolverTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class RateLimitSnapshotResolverTests : IDisposable
{
    private static readonly DateTimeOffset _now = new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
    private readonly string _settingsDirectory = Path.Combine(Path.GetTempPath(), $"RateLimitSnapshotResolverTests_{Guid.NewGuid()}");

    [Fact]
    public async Task ResolveAsync_ShouldReuseCapturedSnapshotWithoutRefetching()
    {
        // ファイルは書き出さない。CaptureAsync 経由で再取得すると必ず null になる状態で
        // capturedSnapshots 側の値がそのまま使われることを確認する（#167 レビュー対応）。
        RateLimitSnapshotResolver resolver = CreateResolver();
        RateLimitSnapshot captured = CreateSnapshot("claude-code", 40);
        var capturedSnapshots = new Dictionary<string, RateLimitSnapshot> { ["claude-code"] = captured };

        IReadOnlyList<RateLimitSnapshot> resolved = await resolver.ResolveAsync(["claude-code"], capturedSnapshots, CancellationToken.None);

        resolved.Should().ContainSingle().Which.Should().BeSameAs(captured);
    }

    [Fact]
    public async Task ResolveAsync_ShouldFetchUncapturedAgentFromSnapshotService()
    {
        await WriteSnapshotAsync("agy", 55);
        RateLimitSnapshotResolver resolver = CreateResolver();

        IReadOnlyList<RateLimitSnapshot> resolved = await resolver.ResolveAsync(["agy"], new Dictionary<string, RateLimitSnapshot>(), CancellationToken.None);

        resolved.Should().ContainSingle(snapshot => snapshot.AgentId == "agy");
    }

    [Fact]
    public async Task ResolveAsync_ShouldExcludeCapturedSnapshotWhenAgentIdMismatches()
    {
        // capturedSnapshots のキー（agentId）と snapshot 自身の AgentId が食い違う場合、
        // 辞書引きだけで採用すると別 agent の snapshot が混入する（レビュー指摘対応）。
        RateLimitSnapshotResolver resolver = CreateResolver();
        RateLimitSnapshot mismatched = CreateSnapshot("agy", 40);
        var capturedSnapshots = new Dictionary<string, RateLimitSnapshot> { ["claude-code"] = mismatched };

        IReadOnlyList<RateLimitSnapshot> resolved = await resolver.ResolveAsync(["claude-code"], capturedSnapshots, CancellationToken.None);

        resolved.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_ShouldExcludeFetchedSnapshotWhenAgentIdMismatches()
    {
        await WriteSnapshotAsync("claude-code", 40, payloadAgentId: "agy");
        RateLimitSnapshotResolver resolver = CreateResolver();

        IReadOnlyList<RateLimitSnapshot> resolved = await resolver.ResolveAsync(["claude-code"], new Dictionary<string, RateLimitSnapshot>(), CancellationToken.None);

        resolved.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_ShouldSkipAgentWhenSnapshotReadFails()
    {
        await WriteSnapshotAsync("claude-code", 40);
        await WriteSnapshotAsync("agy", 55);
        RateLimitSnapshotResolver resolver = CreateResolver();
        string lockedPath = Path.Combine(_settingsDirectory, "ratelimit-status", "claude-code.json");
        using FileStream exclusiveLock = new(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);

        IReadOnlyList<RateLimitSnapshot> resolved = await resolver.ResolveAsync(
            ["claude-code", "agy"],
            new Dictionary<string, RateLimitSnapshot>(),
            CancellationToken.None);

        resolved.Should().ContainSingle(snapshot => snapshot.AgentId == "agy");
    }

    [Fact]
    public async Task ResolveAsync_ShouldPropagateCancellation()
    {
        await WriteSnapshotAsync("claude-code", 40);
        RateLimitSnapshotResolver resolver = CreateResolver();
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        Func<Task> act = () => resolver.ResolveAsync(["claude-code"], new Dictionary<string, RateLimitSnapshot>(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_settingsDirectory))
        {
            Directory.Delete(_settingsDirectory, recursive: true);
        }
    }

    private RateLimitSnapshotResolver CreateResolver()
        => new(new RateLimitSnapshotService(new RateLimitFileService(_settingsDirectory)));

    private static RateLimitSnapshot CreateSnapshot(string agentId, double usedPercentage)
        => new(
            agentId,
            _now,
            [new RateLimitInfo { Id = "five-hour", Label = "5時間枠", ResetAt = _now.AddHours(5), UsedPercentage = usedPercentage }]);

    private async Task WriteSnapshotAsync(string agentId, double usedPercentage, string? payloadAgentId = null)
    {
        string directory = Path.Combine(_settingsDirectory, "ratelimit-status");
        Directory.CreateDirectory(directory);
        string json = $$"""
            {"schemaVersion":1,"agentId":"{{payloadAgentId ?? agentId}}","observedAt":"{{_now:O}}","limits":[{"id":"five-hour","label":"5時間枠","resetAt":"{{_now.AddHours(5):O}}","usedPercentage":{{usedPercentage.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}]}
            """;
        await File.WriteAllTextAsync(Path.Combine(directory, $"{agentId}.json"), json);
    }
}
