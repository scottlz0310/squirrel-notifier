// <copyright file="RateLimitSnapshotServiceTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.IO;
using System.Threading;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class RateLimitSnapshotServiceTests : IDisposable
{
    private readonly string _settingsDirectory;

    public RateLimitSnapshotServiceTests()
    {
        _settingsDirectory = Path.Combine(Path.GetTempPath(), $"RateLimitSnapshotServiceTests_{Guid.NewGuid()}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_settingsDirectory))
        {
            Directory.Delete(_settingsDirectory, true);
        }
    }

    [Fact]
    public async Task CaptureAsync_FileNotExist_ShouldReturnNull()
    {
        var service = new RateLimitSnapshotService(new RateLimitFileService(_settingsDirectory));

        RateLimitSnapshot? result = await service.CaptureAsync("claude-code", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task CaptureAsync_NewSchemaFile_ShouldReturnSnapshot()
    {
        string dir = Path.Combine(_settingsDirectory, "ratelimit-status");
        Directory.CreateDirectory(dir);
        string json = "{\"schemaVersion\":1,\"agentId\":\"claude-code\",\"observedAt\":\"2026-07-11T10:00:00Z\"," +
            "\"limits\":[{\"id\":\"5h\",\"label\":\"5時間枠\",\"resetAt\":\"2026-07-11T15:00:00Z\",\"usedPercentage\":50}]}";
        await File.WriteAllTextAsync(Path.Combine(dir, "claude-code.json"), json);

        var service = new RateLimitSnapshotService(new RateLimitFileService(_settingsDirectory));

        RateLimitSnapshot? result = await service.CaptureAsync("claude-code", CancellationToken.None);

        result.Should().NotBeNull();
        result!.AgentId.Should().Be("claude-code");
        result.Limits.Should().ContainSingle();
    }

    [Fact]
    public async Task CaptureAsync_LegacySchemaFile_ShouldReturnNull()
    {
        string dir = Path.Combine(_settingsDirectory, "ratelimit-status");
        Directory.CreateDirectory(dir);
        string json = "{\"limits\":[{\"id\":\"5h\",\"label\":\"5時間枠\",\"resetAt\":\"2026-07-11T15:00:00Z\"}]}";
        await File.WriteAllTextAsync(Path.Combine(dir, "claude-code.json"), json);

        var service = new RateLimitSnapshotService(new RateLimitFileService(_settingsDirectory));

        RateLimitSnapshot? result = await service.CaptureAsync("claude-code", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenFileServiceIsNull()
    {
        Action act = () => _ = new RateLimitSnapshotService(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
