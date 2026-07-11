// <copyright file="CodexAppServerRateLimitClientTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Moq;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class CodexAppServerRateLimitClientTests
{
    private static readonly DateTimeOffset _now = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
    private const long _resetsAtPrimary = 1783768226;
    private const long _resetsAtSecondary = 1784355026;

    private const string _initializeResponseLine = """{"id":1,"result":{"userAgent":"test"}}""";

    private const string _sampleReadResultJson = """
        {"rateLimits":{"limitId":"codex","primary":{"usedPercent":100,"windowDurationMins":300,"resetsAt":1783768226},"secondary":{"usedPercent":16,"windowDurationMins":10080,"resetsAt":1784355026}},"rateLimitsByLimitId":{"codex":{"limitId":"codex","primary":{"usedPercent":100,"windowDurationMins":300,"resetsAt":1783768226},"secondary":{"usedPercent":16,"windowDurationMins":10080,"resetsAt":1784355026}}}}
        """;

    // ---- Normalize（正規化ルール）----

    [Fact]
    public void Normalize_ShouldExpandPrimaryAndSecondaryWindowsFromMultiBucketView()
    {
        CodexRateLimitsReadResult result = Deserialize(_sampleReadResultJson);

        RateLimitSnapshot? snapshot = CodexAppServerRateLimitClient.Normalize("codex", result, _now);

        snapshot.Should().NotBeNull();
        snapshot!.AgentId.Should().Be("codex");
        snapshot.ObservedAt.Should().Be(_now);
        snapshot.Limits.Should().HaveCount(2);
        snapshot.Limits[0].Id.Should().Be("codex:primary");
        snapshot.Limits[0].Label.Should().Be("5時間枠");
        snapshot.Limits[0].UsedPercentage.Should().Be(100);
        snapshot.Limits[0].ResetAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(_resetsAtPrimary));
        snapshot.Limits[1].Id.Should().Be("codex:secondary");
        snapshot.Limits[1].Label.Should().Be("7日枠");
        snapshot.Limits[1].UsedPercentage.Should().Be(16);
        snapshot.Limits[1].ResetAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(_resetsAtSecondary));
    }

    [Fact]
    public void Normalize_ShouldFallBackToSingleBucketViewWhenByLimitIdIsMissing()
    {
        CodexRateLimitsReadResult result = Deserialize(
            """{"rateLimits":{"limitId":"codex","primary":{"usedPercent":42,"windowDurationMins":300,"resetsAt":1783768226}}}""");

        RateLimitSnapshot? snapshot = CodexAppServerRateLimitClient.Normalize("codex", result, _now);

        snapshot!.Limits.Should().ContainSingle();
        snapshot.Limits[0].Id.Should().Be("codex:primary");
        snapshot.Limits[0].UsedPercentage.Should().Be(42);
    }

    [Fact]
    public void Normalize_ShouldFallBackToAgentIdWhenLimitIdIsMissing()
    {
        CodexRateLimitsReadResult result = Deserialize(
            """{"rateLimits":{"primary":{"usedPercent":10,"windowDurationMins":300,"resetsAt":1783768226}}}""");

        RateLimitSnapshot? snapshot = CodexAppServerRateLimitClient.Normalize("codex", result, _now);

        snapshot!.Limits[0].Id.Should().Be("codex:primary");
    }

    [Fact]
    public void Normalize_ShouldReturnNullWhenNoUsableWindowExists()
    {
        CodexRateLimitsReadResult empty = Deserialize("""{"rateLimits":null,"rateLimitsByLimitId":null}""");

        CodexAppServerRateLimitClient.Normalize("codex", empty, _now).Should().BeNull();
    }

    [Theory]
    [InlineData("""{"usedPercent":50,"windowDurationMins":300}""")]
    [InlineData("""{"resetsAt":1783768226,"windowDurationMins":300}""")]
    public void Normalize_ShouldSkipWindowMissingRequiredFields(string windowJson)
    {
        // ResetAt は既存モデル（RateLimitInfo.Validate）で必須のため、
        // resetsAt / usedPercent を欠く window は取得不可として除外される
        CodexRateLimitsReadResult result = Deserialize(
            "{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":" + windowJson + "}}");

        CodexAppServerRateLimitClient.Normalize("codex", result, _now).Should().BeNull();
    }

    [Fact]
    public void Normalize_ShouldClampUsedPercentIntoValidRange()
    {
        CodexRateLimitsReadResult result = Deserialize(
            """{"rateLimits":{"limitId":"codex","primary":{"usedPercent":120,"windowDurationMins":300,"resetsAt":1783768226}}}""");

        RateLimitSnapshot? snapshot = CodexAppServerRateLimitClient.Normalize("codex", result, _now);

        snapshot!.Limits[0].UsedPercentage.Should().Be(100);
    }

    [Theory]
    [InlineData(300, "5時間枠")]
    [InlineData(10080, "7日枠")]
    [InlineData(2880, "2日枠")]
    [InlineData(90, "90分枠")]
    [InlineData(null, "primary 枠")]
    public void Normalize_ShouldDeriveLabelFromWindowDuration(int? windowDurationMins, string expectedLabel)
    {
        string duration = windowDurationMins is int mins ? mins.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null";
        CodexRateLimitsReadResult result = Deserialize(
            "{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":10,\"windowDurationMins\":" + duration + ",\"resetsAt\":1783768226}}}");

        RateLimitSnapshot? snapshot = CodexAppServerRateLimitClient.Normalize("codex", result, _now);

        snapshot!.Limits[0].Label.Should().Be(expectedLabel);
    }

    // ---- CaptureAsync（JSON-RPC round-trip）----

    [Fact]
    public async Task CaptureAsync_ShouldReturnNormalizedSnapshotAndSendJsonRpcRequests()
    {
        (Mock<IProcessInstance> process, MemoryStream stdin) = CreateMockProcess(
            BuildStdout(_initializeResponseLine, $$"""{"id":2,"result":{{_sampleReadResultJson.Trim()}}}"""));
        CodexAppServerRateLimitClient client = CreateClient(process, out Mock<IProcessRunner> runner);

        RateLimitSnapshot? snapshot = await client.CaptureAsync("codex", CancellationToken.None);

        snapshot.Should().NotBeNull();
        snapshot!.ObservedAt.Should().Be(_now);
        snapshot.Limits.Should().HaveCount(2);

        string sentPayload = Encoding.UTF8.GetString(stdin.ToArray());
        sentPayload.Should().Contain("\"method\":\"initialize\"").And.Contain("squirrel-notifier");
        sentPayload.Should().Contain("\"method\":\"account/rateLimits/read\"");
        runner.Verify(r => r.Start(It.Is<ProcessStartInfo>(p => p.FileName == "codex" && p.Arguments == "app-server")), Times.Once);
        process.Verify(p => p.Kill(true), Times.Once);
    }

    [Fact]
    public async Task CaptureAsync_ShouldSkipNotificationsAndNonJsonLines()
    {
        (Mock<IProcessInstance> process, _) = CreateMockProcess(BuildStdout(
            "not-json noise",
            """{"jsonrpc":"2.0","method":"remoteControl/status/changed","params":{}}""",
            _initializeResponseLine,
            """{"jsonrpc":"2.0","method":"account/rateLimits/updated","params":{}}""",
            $$"""{"id":2,"result":{{_sampleReadResultJson.Trim()}}}"""));
        CodexAppServerRateLimitClient client = CreateClient(process, out _);

        RateLimitSnapshot? snapshot = await client.CaptureAsync("codex", CancellationToken.None);

        snapshot.Should().NotBeNull();
    }

    [Fact]
    public async Task CaptureAsync_ShouldReturnNullWhenReadRespondsWithJsonRpcError()
    {
        (Mock<IProcessInstance> process, _) = CreateMockProcess(BuildStdout(
            _initializeResponseLine,
            """{"id":2,"error":{"code":-32000,"message":"not logged in"}}"""));
        CodexAppServerRateLimitClient client = CreateClient(process, out _);

        RateLimitSnapshot? snapshot = await client.CaptureAsync("codex", CancellationToken.None);

        snapshot.Should().BeNull();
    }

    [Fact]
    public async Task CaptureAsync_ShouldReturnNullWhenServerExitsWithoutResponse()
    {
        (Mock<IProcessInstance> process, _) = CreateMockProcess(string.Empty);
        CodexAppServerRateLimitClient client = CreateClient(process, out _);

        RateLimitSnapshot? snapshot = await client.CaptureAsync("codex", CancellationToken.None);

        snapshot.Should().BeNull();
    }

    [Fact]
    public async Task CaptureAsync_ShouldReturnNullWhenProcessStartFails()
    {
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Throws(new System.ComponentModel.Win32Exception("codex not found"));
        CodexAppServerRateLimitClient client = new(runner.Object, new FixedTimeProvider(_now));

        RateLimitSnapshot? snapshot = await client.CaptureAsync("codex", CancellationToken.None);

        snapshot.Should().BeNull();
    }

    [Fact]
    public async Task CaptureAsync_ShouldReturnNullWhenRoundTripTimesOut()
    {
        (Mock<IProcessInstance> process, _) = CreateMockProcess(stdoutStream: new BlockingStream());
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(process.Object);
        CodexAppServerRateLimitClient client = new(
            runner.Object, new FixedTimeProvider(_now), roundTripTimeout: TimeSpan.FromMilliseconds(200));

        RateLimitSnapshot? snapshot = await client.CaptureAsync("codex", CancellationToken.None);

        snapshot.Should().BeNull();
        process.Verify(p => p.Kill(true), Times.Once);
    }

    [Fact]
    public async Task CaptureAsync_ShouldPropagateExternalCancellation()
    {
        (Mock<IProcessInstance> process, _) = CreateMockProcess(stdoutStream: new BlockingStream());
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(process.Object);
        CodexAppServerRateLimitClient client = new(runner.Object, new FixedTimeProvider(_now));
        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        Func<Task> act = () => client.CaptureAsync("codex", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static CodexRateLimitsReadResult Deserialize(string json)
        => System.Text.Json.JsonSerializer.Deserialize<CodexRateLimitsReadResult>(json)!;

    private static string BuildStdout(params string[] lines) => string.Join('\n', lines) + "\n";

    private static CodexAppServerRateLimitClient CreateClient(Mock<IProcessInstance> process, out Mock<IProcessRunner> runner)
    {
        runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(process.Object);
        return new CodexAppServerRateLimitClient(runner.Object, new FixedTimeProvider(_now));
    }

    private static (Mock<IProcessInstance> Process, MemoryStream StdinStream) CreateMockProcess(
        string? stdout = null, Stream? stdoutStream = null)
    {
        var stdin = new MemoryStream();
        var mock = new Mock<IProcessInstance>();
        mock.SetupGet(p => p.StandardOutput).Returns(new StreamReader(
            stdoutStream ?? new MemoryStream(Encoding.UTF8.GetBytes(stdout ?? string.Empty))));
        mock.SetupGet(p => p.StandardError).Returns(new StreamReader(new MemoryStream()));
        mock.SetupGet(p => p.StandardInput).Returns(new StreamWriter(stdin));
        return (mock, stdin);
    }

    // 応答を一切返さずキャンセルまでブロックする stdout。タイムアウト・キャンセル経路の検証用
    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
