// <copyright file="GatewayConnectionTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Net;
using System.Text;
using FluentAssertions;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Integration;

// 現行 compose 経由の結合テスト。
// 既定（環境変数 MCP_GATEWAY_URL 未設定）ではスキップし、CI を壊さない。
// 実環境で route を含む Gateway URL を指定して実行すると、構築した URL が
// MCP endpoint に到達する（route が 404 にならない）ことを検証する。
public class GatewayConnectionTests
{
    private const string GatewayUrlEnvVar = "MCP_GATEWAY_URL";

    [Fact]
    public async Task ResourcesList_AtConfiguredGatewayUrl_DoesNotReturn404()
    {
        string? gatewayUrl = Environment.GetEnvironmentVariable(GatewayUrlEnvVar);
        if (string.IsNullOrWhiteSpace(gatewayUrl))
        {
            // 環境変数が未設定の場合はスキップ（CI 既定）。
            return;
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Post, gatewayUrl);
        _ = request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Content = new StringContent(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"resources/list\",\"params\":{}}",
            Encoding.UTF8,
            "application/json");

        HttpResponseMessage response = await client.SendAsync(request);

        // route が誤っている場合、gateway root は 404 を返す（Issue #102）。
        // 正しい route なら 404 以外（200 や JSON-RPC エラー応答を含む）になる。
        response.StatusCode.Should().NotBe(
            HttpStatusCode.NotFound,
            $"'{gatewayUrl}' は MCP endpoint に到達する必要があります（route が正しいこと）");
    }
}
