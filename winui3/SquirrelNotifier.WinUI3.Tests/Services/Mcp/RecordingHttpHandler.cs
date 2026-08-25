// <copyright file="RecordingHttpHandler.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Net;
using System.Text.Json;

namespace SquirrelNotifier.WinUI3.Tests.Services.Mcp;

/// <summary>
/// MCP のやり取りを記録するだけの <see cref="DelegatingHandler"/>。
/// </summary>
/// <remarks>
/// protocol の応答は一切生成せず、後段の <see cref="McpTestServer"/> に素通しする。
/// 責務は認証ヘッダー・protocol ヘッダー・JSON-RPC method 名の観測に限定する（#238）。
/// </remarks>
internal sealed class RecordingHttpHandler : DelegatingHandler
{
    private readonly List<RecordedExchange> _exchanges = [];
    private readonly Lock _lock = new();

    public RecordingHttpHandler(HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
    }

    public IReadOnlyList<RecordedExchange> Exchanges
    {
        get
        {
            lock (_lock)
            {
                return [.. _exchanges];
            }
        }
    }

    /// <summary>
    /// 記録された JSON-RPC method 名を送信順に返す。notification など method を持たない
    /// リクエストは空文字になる.
    /// </summary>
    public IReadOnlyList<string> Methods => [.. Exchanges.Select(e => e.Method)];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // ヘッダーは応答後に読み取れなくなる場合があるため、送信前にスナップショットを取る。
        RecordedRequest recorded = new(
            request.Method,
            ExtractMethod(body),
            request.Headers.Authorization?.Parameter,
            request.Headers.Authorization?.Scheme,
            SnapshotHeaders(request));

        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        lock (_lock)
        {
            _exchanges.Add(new RecordedExchange(recorded, response.StatusCode));
        }

        return response;
    }

    private static Dictionary<string, string[]> SnapshotHeaders(HttpRequestMessage request)
    {
        return request.Headers.ToDictionary(
            header => header.Key,
            header => header.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string ExtractMethod(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("method", out JsonElement method)
                ? method.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}

internal sealed record RecordedRequest(
    HttpMethod HttpMethod,
    string Method,
    string? BearerToken,
    string? AuthorizationScheme,
    IReadOnlyDictionary<string, string[]> Headers)
{
    public bool HasHeader(string name) => Headers.ContainsKey(name);

    public string? SingleHeaderValue(string name) =>
        Headers.TryGetValue(name, out string[]? values) && values.Length == 1 ? values[0] : null;
}

internal sealed record RecordedExchange(RecordedRequest Request, HttpStatusCode StatusCode)
{
    public string Method => Request.Method;
}
