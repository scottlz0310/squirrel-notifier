// <copyright file="McpResourceProbeProtocolTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using ModelContextProtocol;
using SquirrelNotifier.WinUI3.Services;
using SquirrelNotifier.WinUI3.Tests.Services.Mcp;

namespace SquirrelNotifier.WinUI3.Tests.Services;

/// <summary>
/// protocol negotiation の互換性を検証する。本線は discovery-first の 2026-07-28 のみで、
/// legacy への自動降格は起こしてはならない（#238）.
/// </summary>
public sealed class McpResourceProbeProtocolTests
{
    [Fact]
    public async Task FetchResourceUrisAsync_SendsServerDiscoverBeforeResourcesList()
    {
        await using McpTestServer server = await McpTestServer.StartAsync(TestResource.ReviewQueue);
        using RecordingHttpHandler handler = server.CreateRecordingHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var probe = new McpResourceProbe(() => httpClient);

        await probe.FetchResourceUrisAsync(McpTestServer.Endpoint, null, CancellationToken.None);

        List<string> methods = [.. handler.Methods];
        methods.Should().NotBeEmpty();
        methods[0].Should().Be("server/discover");
        methods.Should().Contain("resources/list");
        methods.IndexOf("server/discover").Should().BeLessThan(methods.IndexOf("resources/list"));
    }

    [Fact]
    public async Task FetchResourceUrisAsync_DoesNotUseInitializeHandshake()
    {
        await using McpTestServer server = await McpTestServer.StartAsync(TestResource.ReviewQueue);
        using RecordingHttpHandler handler = server.CreateRecordingHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var probe = new McpResourceProbe(() => httpClient);

        await probe.FetchResourceUrisAsync(McpTestServer.Endpoint, null, CancellationToken.None);

        handler.Methods.Should().NotContain("initialize");
        handler.Methods.Should().NotContain("notifications/initialized");
    }

    [Fact]
    public async Task FetchResourceUrisAsync_NeverSendsSessionIdHeader()
    {
        await using McpTestServer server = await McpTestServer.StartAsync(TestResource.ReviewQueue);
        using RecordingHttpHandler handler = server.CreateRecordingHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var probe = new McpResourceProbe(() => httpClient);

        await probe.FetchResourceUrisAsync(McpTestServer.Endpoint, null, CancellationToken.None);

        handler.Exchanges.Should().NotBeEmpty();
        handler.Exchanges.Should().AllSatisfy(exchange =>
            exchange.Request.HasHeader("Mcp-Session-Id").Should().BeFalse());
    }

    [Fact]
    public async Task FetchResourceUrisAsync_DeclaresPinnedProtocolVersionOnEveryRequest()
    {
        await using McpTestServer server = await McpTestServer.StartAsync(TestResource.ReviewQueue);
        using RecordingHttpHandler handler = server.CreateRecordingHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var probe = new McpResourceProbe(() => httpClient);

        await probe.FetchResourceUrisAsync(McpTestServer.Endpoint, null, CancellationToken.None);

        handler.Exchanges.Should().NotBeEmpty();
        handler.Exchanges.Should().AllSatisfy(exchange =>
            exchange.Request.SingleHeaderValue("MCP-Protocol-Version")
                .Should().Be(McpResourceProbe.PinnedProtocolVersion));
    }

    [Fact]
    public async Task FetchResourceUrisAsync_AgainstLegacyOnlyServer_FailsWithoutDowngrading()
    {
        await using McpTestServer server = await McpTestServer.StartLegacyOnlyAsync(TestResource.ReviewQueue);
        using RecordingHttpHandler handler = server.CreateRecordingHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var probe = new McpResourceProbe(() => httpClient);

        Func<Task> act = () => probe.FetchResourceUrisAsync(
            McpTestServer.Endpoint,
            null,
            CancellationToken.None);

        await act.Should().ThrowAsync<McpException>();

        // 降格していれば initialize が続くはずなので、server/discover で止まっていることを固定する。
        handler.Methods.Should().ContainSingle().Which.Should().Be("server/discover");
    }

    [Fact]
    public async Task GetUserMessage_ForLegacyOnlyServerFailure_ExplainsNegotiationFailure()
    {
        await using McpTestServer server = await McpTestServer.StartLegacyOnlyAsync(TestResource.ReviewQueue);
        using RecordingHttpHandler handler = server.CreateRecordingHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var probe = new McpResourceProbe(() => httpClient);

        Exception ex = await Assert.ThrowsAnyAsync<Exception>(() => probe.FetchResourceUrisAsync(
            McpTestServer.Endpoint,
            null,
            CancellationToken.None));

        string message = McpResourceProbe.GetUserMessage(ex);

        message.Should().Contain(McpResourceProbe.PinnedProtocolVersion);
        message.Should().Contain("ネゴシエーション");
    }

    /// <summary>
    /// mcp-gateway の OAuth 境界で 401 になった場合、version を固定していても認証エラーとして
    /// 届くことを固定する。protocol negotiation の失敗に丸められると、token 切れの診断ができなくなる.
    /// </summary>
    [Fact]
    public async Task FetchResourceUrisAsync_WhenGatewayRejectsWithUnauthorized_IsReportedAsAuthError()
    {
        await using McpTestServer server = await McpTestServer.StartRequiringAuthorizationAsync(
            TestResource.ReviewQueue);
        using RecordingHttpHandler handler = server.CreateRecordingHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var probe = new McpResourceProbe(() => httpClient);

        Exception ex = await Assert.ThrowsAnyAsync<Exception>(() => probe.FetchResourceUrisAsync(
            McpTestServer.Endpoint,
            null,
            CancellationToken.None));

        McpResourceProbe.GetUserMessage(ex).Should().Contain("認証エラー");
    }
}
