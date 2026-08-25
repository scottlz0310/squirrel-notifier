// <copyright file="McpResourceProbeResourceTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Services;
using SquirrelNotifier.WinUI3.Tests.Services.Mcp;

namespace SquirrelNotifier.WinUI3.Tests.Services;

/// <summary>
/// resource の一覧取得・読み出しの振る舞いを、2026-07-28 対応の実 SDK サーバーに対して検証する.
/// </summary>
public sealed class McpResourceProbeResourceTests
{
    [Fact]
    public async Task FetchResourceUrisAsync_ReturnsAdvertisedResourceUris()
    {
        await using McpTestServer server = await McpTestServer.StartAsync(
            TestResource.ReviewQueue,
            TestResource.ReReviewRequests);
        using RecordingHttpHandler handler = server.CreateRecordingHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var probe = new McpResourceProbe(() => httpClient);

        IReadOnlyList<string> uris = await probe.FetchResourceUrisAsync(
            McpTestServer.Endpoint,
            null,
            CancellationToken.None);

        uris.Should().BeEquivalentTo(
        [
            "queue://review/queue",
            "queue://review/re-review-requests",
        ]);
    }

    [Fact]
    public async Task FetchResourceUrisAsync_WhenServerExposesNoResources_ReturnsEmptyList()
    {
        await using McpTestServer server = await McpTestServer.StartWithNoResourcesAsync();
        using RecordingHttpHandler handler = server.CreateRecordingHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var probe = new McpResourceProbe(() => httpClient);

        IReadOnlyList<string> uris = await probe.FetchResourceUrisAsync(
            McpTestServer.Endpoint,
            null,
            CancellationToken.None);

        uris.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadResourceTextAsync_ReturnsTextContentOfRequestedResource()
    {
        const string ExpectedJson =
            "{\"limits\":[{\"id\":\"5h\",\"label\":\"5時間制限\",\"resetAt\":\"2026-07-05T20:00:00Z\"}]}";
        var rateLimit = new TestResource("ratelimit://status/claude", "Claude Rate Limit", ExpectedJson);

        await using McpTestServer server = await McpTestServer.StartAsync(TestResource.ReviewQueue, rateLimit);
        using RecordingHttpHandler handler = server.CreateRecordingHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var probe = new McpResourceProbe(() => httpClient);

        string result = await probe.ReadResourceTextAsync(
            McpTestServer.Endpoint,
            null,
            rateLimit.Uri,
            CancellationToken.None);

        result.Should().Be(ExpectedJson);
    }

    [Fact]
    public async Task ReadResourceTextAsync_WhenResourceIsUnknown_Throws()
    {
        await using McpTestServer server = await McpTestServer.StartAsync(TestResource.ReviewQueue);
        using RecordingHttpHandler handler = server.CreateRecordingHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var probe = new McpResourceProbe(() => httpClient);

        Func<Task> act = () => probe.ReadResourceTextAsync(
            McpTestServer.Endpoint,
            null,
            "queue://review/does-not-exist",
            CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
    }
}
