// <copyright file="CacheServiceTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.IO;
using System.Text.Json;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class CacheServiceTests : IDisposable
{
    private readonly string _cacheDirectory;

    public CacheServiceTests()
    {
        _cacheDirectory = Path.Combine(Path.GetTempPath(), $"CacheServiceTests_{Guid.NewGuid()}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheDirectory))
        {
            Directory.Delete(_cacheDirectory, true);
        }
    }

    [Fact]
    public async Task LoadAsync_ShouldReturnEmptyCache_WhenFileDoesNotExist()
    {
        var service = new CacheService(_cacheDirectory);

        NotificationCache result = await service.LoadAsync();

        result.SeenEventIds.Should().BeEmpty();
        result.RecentEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_ShouldRoundtripData()
    {
        var service = new CacheService(_cacheDirectory);
        var cache = new NotificationCache
        {
            SeenEventIds = new List<string> { "evt_1", "evt_2" },
            RecentEvents = new List<CachedReviewEvent>
            {
                new()
                {
                    EventId = "evt_1",
                    Repository = "owner/repo",
                    PrNumber = 42,
                    PrUrl = "https://github.com/owner/repo/pull/42",
                    Reason = "review_requested",
                    Message = "Review requested",
                    ReceivedTime = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Local),
                },
            },
        };

        await service.SaveAsync(cache);
        NotificationCache loaded = await service.LoadAsync();

        loaded.SeenEventIds.Should().BeEquivalentTo(new[] { "evt_1", "evt_2" });
        loaded.RecentEvents.Should().HaveCount(1);
        loaded.RecentEvents[0].EventId.Should().Be("evt_1");
        loaded.RecentEvents[0].Repository.Should().Be("owner/repo");
        loaded.RecentEvents[0].PrNumber.Should().Be(42);
    }

    [Fact]
    public async Task LoadAsync_ShouldReturnEmptyCache_WhenFileIsCorrupted()
    {
        Directory.CreateDirectory(_cacheDirectory);
        string cachePath = Path.Combine(_cacheDirectory, "cache.json");
        await File.WriteAllTextAsync(cachePath, "{ corrupted json %%%");

        var service = new CacheService(_cacheDirectory);

        NotificationCache result = await service.LoadAsync();

        result.SeenEventIds.Should().BeEmpty();
        result.RecentEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_ShouldWriteAtomically_ViaTempFile()
    {
        var service = new CacheService(_cacheDirectory);
        var cache = new NotificationCache
        {
            SeenEventIds = new List<string> { "evt_atomic" },
        };

        await service.SaveAsync(cache);

        string cachePath = Path.Combine(_cacheDirectory, "cache.json");
        string tempPath = cachePath + ".tmp";

        // 正常完了後は .tmp が残らない
        File.Exists(cachePath).Should().BeTrue();
        File.Exists(tempPath).Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_ShouldOverwritePreviousCache()
    {
        var service = new CacheService(_cacheDirectory);

        var cache1 = new NotificationCache { SeenEventIds = new List<string> { "old_id" } };
        await service.SaveAsync(cache1);

        var cache2 = new NotificationCache { SeenEventIds = new List<string> { "new_id" } };
        await service.SaveAsync(cache2);

        NotificationCache loaded = await service.LoadAsync();
        loaded.SeenEventIds.Should().BeEquivalentTo(new[] { "new_id" });
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenDirectoryIsEmpty()
    {
        Action act = () => _ = new CacheService(string.Empty);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task LoadAsync_ShouldReturnEmptyCache_WhenFileIsEmptyJson()
    {
        Directory.CreateDirectory(_cacheDirectory);
        string cachePath = Path.Combine(_cacheDirectory, "cache.json");
        await File.WriteAllTextAsync(cachePath, "null");

        var service = new CacheService(_cacheDirectory);

        NotificationCache result = await service.LoadAsync();

        result.SeenEventIds.Should().BeEmpty();
        result.RecentEvents.Should().BeEmpty();
    }
}
