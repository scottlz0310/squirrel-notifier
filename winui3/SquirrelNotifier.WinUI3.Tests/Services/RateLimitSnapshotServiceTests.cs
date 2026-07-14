// <copyright file="RateLimitSnapshotServiceTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using FluentAssertions;
using Moq;
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

    [Fact]
    public async Task CaptureAsync_Codex_ShouldUseAppServerClientInsteadOfLocalFile()
    {
        // ローカルファイルを一切用意していなくても App Server 経路から取得できる（#163）
        string stdout =
            "{\"id\":1,\"result\":{}}\n" +
            "{\"id\":2,\"result\":{\"rateLimits\":{\"limitId\":\"codex\"," +
            "\"primary\":{\"usedPercent\":55,\"windowDurationMins\":300,\"resetsAt\":1783768226}}}}\n";
        var process = new Mock<IProcessInstance>();
        process.SetupGet(p => p.StandardOutput).Returns(new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(stdout))));
        process.SetupGet(p => p.StandardError).Returns(new StreamReader(new MemoryStream()));
        process.SetupGet(p => p.StandardInput).Returns(new StreamWriter(new MemoryStream()));
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(process.Object);
        var service = new RateLimitSnapshotService(
            new RateLimitFileService(_settingsDirectory),
            new CodexAppServerRateLimitClient(runner.Object));

        RateLimitSnapshot? result = await service.CaptureAsync(RateLimitSnapshotService.CodexAgentId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.AgentId.Should().Be("codex");
        result.Limits.Should().ContainSingle().Which.UsedPercentage.Should().Be(55);
    }

    [Fact]
    public async Task CaptureAsync_Codex_ShouldReturnNull_WhenAppServerIsUnavailable()
    {
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Throws(new System.ComponentModel.Win32Exception("codex not found"));
        var service = new RateLimitSnapshotService(
            new RateLimitFileService(_settingsDirectory),
            new CodexAppServerRateLimitClient(runner.Object));

        RateLimitSnapshot? result = await service.CaptureAsync(RateLimitSnapshotService.CodexAgentId, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task CaptureCodexWithFailureReasonAsync_ShouldReturnCommandNotFound_WhenAppServerIsUnavailable()
    {
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Throws(new System.ComponentModel.Win32Exception(2, "codex not found")); // ERROR_FILE_NOT_FOUND
        var service = new RateLimitSnapshotService(
            new RateLimitFileService(_settingsDirectory),
            new CodexAppServerRateLimitClient(runner.Object));

        (RateLimitSnapshot? snapshot, CodexRateLimitFailureReason? failureReason) =
            await service.CaptureCodexWithFailureReasonAsync(RateLimitSnapshotService.CodexAgentId, CancellationToken.None);

        snapshot.Should().BeNull();
        failureReason.Should().Be(CodexRateLimitFailureReason.CommandNotFound);
    }

    [Fact]
    public async Task CaptureCodexWithFailureReasonAsync_ShouldThrow_WhenAgentIdIsNotCodex()
    {
        var service = new RateLimitSnapshotService(new RateLimitFileService(_settingsDirectory));

        Func<Task> act = () => service.CaptureCodexWithFailureReasonAsync("claude-code", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
