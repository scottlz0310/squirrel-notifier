// <copyright file="McpResourceProbe.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Net.Http.Headers;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace SquirrelNotifier.WinUI3.Services;

internal sealed class McpResourceProbe
{
    private readonly Func<HttpClient> _httpClientFactory;

    public McpResourceProbe(Func<HttpClient>? httpClientFactory = null)
    {
        _httpClientFactory = httpClientFactory ?? (() => new HttpClient());
    }

    public async Task<IReadOnlyList<string>> FetchResourceUrisAsync(
        Uri endpoint,
        string? bearerToken,
        CancellationToken cancellationToken)
    {
        using HttpClient httpClient = _httpClientFactory();

        if (!string.IsNullOrEmpty(bearerToken))
        {
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        await using HttpClientTransport transport = new(
            new HttpClientTransportOptions
            {
                Endpoint = endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
            },
            httpClient,
            null,
            false);

        await using McpClient client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken).ConfigureAwait(false);

        IList<McpClientResource> resources = await client.ListResourcesAsync((ModelContextProtocol.RequestOptions?)null, cancellationToken).ConfigureAwait(false);

        List<string> uris = new(resources.Count);
        foreach (McpClientResource resource in resources)
        {
            if (!string.IsNullOrEmpty(resource.Uri))
            {
                uris.Add(resource.Uri);
            }
        }

        return uris.AsReadOnly();
    }

    internal static string GetUserMessage(Exception ex)
    {
        if (ex is OperationCanceledException)
        {
            return "操作がキャンセルされました。";
        }

        string msg = ex.Message;

        if (msg.Contains("401", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
        {
            return "認証エラーが発生しました。MCP_PROBE_AUTH_TOKEN の設定を確認してください。";
        }

        if (msg.Contains("404", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Not Found", StringComparison.OrdinalIgnoreCase))
        {
            return "エンドポイントが見つかりませんでした (404)。Gateway URL が正しいか確認してください。";
        }

        if (msg.Contains("refused", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("ECONNREFUSED", StringComparison.OrdinalIgnoreCase))
        {
            return "mcp-gateway への接続に失敗しました。コンテナが起動しているか確認してください。";
        }

        return $"予期しないエラーが発生しました: {msg}";
    }
}
