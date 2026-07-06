// <copyright file="RateLimitFileServiceTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.IO;
using System.Threading;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class RateLimitFileServiceTests : IDisposable
{
    private readonly string _settingsDirectory;

    public RateLimitFileServiceTests()
    {
        _settingsDirectory = Path.Combine(Path.GetTempPath(), $"RateLimitFileServiceTests_{Guid.NewGuid()}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_settingsDirectory))
        {
            Directory.Delete(_settingsDirectory, true);
        }
    }

    [Fact]
    public async Task ReadAgentStatusAsync_ShouldReturnNull_WhenFileDoesNotExist()
    {
        var service = new RateLimitFileService(_settingsDirectory);

        string? result = await service.ReadAgentStatusAsync("claude-code", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ReadAgentStatusAsync_ShouldReturnContent_WhenFileExists()
    {
        string dir = Path.Combine(_settingsDirectory, "ratelimit-status");
        Directory.CreateDirectory(dir);
        string json = "{\"limits\":[{\"id\":\"five_hour\",\"label\":\"5h\",\"resetAt\":\"2026-07-06T12:00:00Z\"}]}";
        await File.WriteAllTextAsync(Path.Combine(dir, "claude-code.json"), json);

        var service = new RateLimitFileService(_settingsDirectory);

        string? result = await service.ReadAgentStatusAsync("claude-code", CancellationToken.None);

        result.Should().Be(json);
    }

    [Fact]
    public async Task ReadAgentStatusAsync_ShouldBeIsolatedPerAgent()
    {
        string dir = Path.Combine(_settingsDirectory, "ratelimit-status");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "claude-code.json"), "{\"limits\":[]}");

        var service = new RateLimitFileService(_settingsDirectory);

        string? result = await service.ReadAgentStatusAsync("agy", CancellationToken.None);

        result.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ReadAgentStatusAsync_ShouldThrow_WhenAgentIdIsBlank(string agentId)
    {
        var service = new RateLimitFileService(_settingsDirectory);

        Func<Task> act = () => service.ReadAgentStatusAsync(agentId, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenSettingsDirectoryIsEmpty()
    {
        Action act = () => _ = new RateLimitFileService(string.Empty);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildSourceIdentifier_ShouldReturnAgentScheme()
    {
        RateLimitFileService.BuildSourceIdentifier("claude-code").Should().Be("agent://claude-code");
    }
}
