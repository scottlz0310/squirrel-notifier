// <copyright file="McpResourceProbeAuthenticationTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Services;
using SquirrelNotifier.WinUI3.Tests.Services.Mcp;

namespace SquirrelNotifier.WinUI3.Tests.Services;

/// <summary>
/// 認証ヘッダーの付与を検証する。負荷分散のため session を持たない 2026-07-28 では、
/// 認可はリクエストごとの token 検証で完結するため、最初の 1 往復ではなく全 POST に
/// bearer token が載っている必要がある.
/// </summary>
public sealed class McpResourceProbeAuthenticationTests
{
    private const string _token = "test-bearer-token";

    [Fact]
    public async Task FetchResourceUrisAsync_WithBearerToken_SendsAuthorizationOnEveryRequest()
    {
        await using McpTestServer server = await McpTestServer.StartAsync(TestResource.ReviewQueue);
        using RecordingHttpHandler handler = server.CreateRecordingHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var probe = new McpResourceProbe(() => httpClient);

        await probe.FetchResourceUrisAsync(McpTestServer.Endpoint, _token, CancellationToken.None);

        handler.Exchanges.Should().NotBeEmpty();
        handler.Exchanges.Should().AllSatisfy(exchange =>
        {
            exchange.Request.AuthorizationScheme.Should().Be("Bearer");
            exchange.Request.BearerToken.Should().Be(_token);
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task FetchResourceUrisAsync_WithoutToken_SendsNoAuthorizationHeader(string? token)
    {
        await using McpTestServer server = await McpTestServer.StartAsync(TestResource.ReviewQueue);
        using RecordingHttpHandler handler = server.CreateRecordingHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var probe = new McpResourceProbe(() => httpClient);

        await probe.FetchResourceUrisAsync(McpTestServer.Endpoint, token, CancellationToken.None);

        handler.Exchanges.Should().NotBeEmpty();
        handler.Exchanges.Should().AllSatisfy(exchange =>
            exchange.Request.BearerToken.Should().BeNull());
    }

    [Fact]
    public async Task ReadResourceTextAsync_WithBearerToken_SendsAuthorizationOnEveryRequest()
    {
        await using McpTestServer server = await McpTestServer.StartAsync(TestResource.ReviewQueue);
        using RecordingHttpHandler handler = server.CreateRecordingHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var probe = new McpResourceProbe(() => httpClient);

        await probe.ReadResourceTextAsync(
            McpTestServer.Endpoint,
            _token,
            TestResource.ReviewQueue.Uri,
            CancellationToken.None);

        handler.Exchanges.Should().NotBeEmpty();
        handler.Exchanges.Should().AllSatisfy(exchange =>
            exchange.Request.BearerToken.Should().Be(_token));
    }
}
