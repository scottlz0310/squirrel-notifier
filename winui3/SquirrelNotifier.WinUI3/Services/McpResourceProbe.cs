// <copyright file="McpResourceProbe.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Net.Http.Headers;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace SquirrelNotifier.WinUI3.Services;

internal sealed class McpResourceProbe
{
    // MCP protocol revision を 2026-07-28 に固定する（#238）。McpClientOptions.ProtocolVersion を
    // 未指定にすると、SDK は server/discover に応答しないサーバーへ initialize handshake で自動降格し、
    // 旧 protocol での接続を成功として返す。降格したこと自体が呼び出し側から観測できないため、
    // legacy 経路を残さない基盤方針（thread-owl は legacy を reject 済み）と噛み合わない。
    // 固定することで、未移行サーバーへの接続は McpException として即座に失敗する。
    internal const string PinnedProtocolVersion = "2026-07-28";

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
        return await WithClientAsync(
            endpoint,
            bearerToken,
            async (client, ct) =>
            {
                IList<McpClientResource> resources = await client.ListResourcesAsync((ModelContextProtocol.RequestOptions?)null, ct).ConfigureAwait(false);

                List<string> uris = new(resources.Count);
                foreach (McpClientResource resource in resources)
                {
                    if (!string.IsNullOrEmpty(resource.Uri))
                    {
                        uris.Add(resource.Uri);
                    }
                }

                return (IReadOnlyList<string>)uris.AsReadOnly();
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ReadResourceTextAsync(
        Uri endpoint,
        string? bearerToken,
        string resourceUri,
        CancellationToken cancellationToken)
    {
        return await WithClientAsync(
            endpoint,
            bearerToken,
            async (client, ct) =>
            {
                ReadResourceResult result = await client.ReadResourceAsync(new Uri(resourceUri), (ModelContextProtocol.RequestOptions?)null, ct).ConfigureAwait(false);

                foreach (ResourceContents content in result.Contents)
                {
                    if (content is TextResourceContents textContent)
                    {
                        return textContent.Text;
                    }
                }

                return string.Empty;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> WithClientAsync<T>(
        Uri endpoint,
        string? bearerToken,
        Func<McpClient, CancellationToken, Task<T>> action,
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

        await using McpClient client = await McpClient.CreateAsync(
            transport,
            new McpClientOptions
            {
                ProtocolVersion = PinnedProtocolVersion,

                // server/discover の probe timeout（既定 5 秒）は、legacy への降格を素早く行うための
                // 値である。version を固定した本 probe には降格先が無いため、短い probe timeout は
                // 「接続が遅いだけ」を「サーバーが discovery 非対応」と誤断させるだけになる。
                // 実際、Gateway URL の localhost が IPv6 に解決される環境では接続確立に 20 秒以上
                // かかり、既定値では negotiation が必ず失敗する。InitializationTimeout（既定 60 秒）
                // による全体の上限に一本化する。
                DiscoverProbeTimeout = Timeout.InfiniteTimeSpan,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return await action(client, cancellationToken).ConfigureAwait(false);
    }

    internal static string GetUserMessage(Exception ex)
    {
        if (ex is OperationCanceledException)
        {
            return "操作がキャンセルされました。";
        }

        if (IsProtocolNegotiationFailure(ex))
        {
            return $"MCP プロトコル {PinnedProtocolVersion} のネゴシエーションに失敗しました。" +
                "接続先が未対応、認証が通っていない、またはエンドポイントに到達できていない可能性があります。" +
                "Gateway URL・MCP_PROBE_AUTH_TOKEN・mcp-gateway と接続先サーバーのバージョンを確認してください。";
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

    // negotiation の失敗は「サーバーが未対応」と「認証が通っていない」を区別できない。
    // server/discover が 401 で弾かれた場合も、SDK は「サーバーが initialize handshake を
    // 要求している」と解釈して同じ例外を投げるため（mcp-gateway の OAuth 未認証で実測）、
    // メッセージ側で両方の可能性を提示する。
    private static bool IsProtocolNegotiationFailure(Exception ex)
    {
        if (ex is UnsupportedProtocolVersionException)
        {
            return true;
        }

        // pin した revision で negotiation が成立しない場合、SDK 2.2.0 は error code を持たない
        // McpException を投げる。構造化された判定材料が無いため、pin した revision 文字列が
        // メッセージに含まれるかで判定する。
        return ex is McpException && ex.Message.Contains(PinnedProtocolVersion, StringComparison.Ordinal);
    }
}
