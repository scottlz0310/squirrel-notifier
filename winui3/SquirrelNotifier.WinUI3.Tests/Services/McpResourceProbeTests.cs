// <copyright file="McpResourceProbeTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class McpResourceProbeTests
{
    private static readonly (string Uri, string Name)[] _twoResources =
    [
        ("queue://review/queue", "Review Queue"),
        ("queue://review/re-review-requests", "Re-review Requests"),
    ];

    [Fact]
    public async Task FetchResourceUrisAsync_WithBearerToken_SendsAuthorizationOnAllRequests()
    {
        const string Token = "test-bearer-token";
        var handler = new SequencedMcpHandler("session-001", _twoResources);
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var probe = new McpResourceProbe(() => httpClient);

        IReadOnlyList<string> uris = await probe.FetchResourceUrisAsync(
            new Uri("http://localhost:12345/mcp"),
            Token,
            CancellationToken.None);

        uris.Should().BeEquivalentTo(["queue://review/queue", "queue://review/re-review-requests"]);

        handler.Captured
            .Where(c => c.Request.Method == HttpMethod.Post)
            .Should().AllSatisfy(c =>
                c.Request.Headers.Authorization.Should().NotBeNull()
                    .And.Subject.As<System.Net.Http.Headers.AuthenticationHeaderValue>()
                    .Parameter.Should().Be(Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task FetchResourceUrisAsync_WithoutToken_NoAuthorizationHeader(string? token)
    {
        var handler = new SequencedMcpHandler("session-002", _twoResources);
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var probe = new McpResourceProbe(() => httpClient);

        await probe.FetchResourceUrisAsync(
            new Uri("http://localhost:12345/mcp"),
            token,
            CancellationToken.None);

        handler.Captured
            .Where(c => c.Request.Method == HttpMethod.Post)
            .Should().AllSatisfy(c =>
                c.Request.Headers.Authorization.Should().BeNull());
    }

    [Fact]
    public async Task FetchResourceUrisAsync_InitializeSentBeforeResourcesList()
    {
        var handler = new SequencedMcpHandler("session-003", _twoResources);
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var probe = new McpResourceProbe(() => httpClient);

        await probe.FetchResourceUrisAsync(
            new Uri("http://localhost:12345/mcp"),
            null,
            CancellationToken.None);

        List<string> methods = handler.Captured
            .Where(c => c.Request.Method == HttpMethod.Post)
            .Select(c => c.Method)
            .ToList();

        methods.Should().NotBeEmpty();
        methods[0].Should().Be("initialize");
        methods.Should().Contain("resources/list");
        methods.IndexOf("initialize").Should().BeLessThan(methods.IndexOf("resources/list"));
    }

    [Fact]
    public async Task FetchResourceUrisAsync_SessionIdPropagatedAfterInitialize()
    {
        const string SessionId = "session-test-007";
        var handler = new SequencedMcpHandler(SessionId, _twoResources);
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var probe = new McpResourceProbe(() => httpClient);

        await probe.FetchResourceUrisAsync(
            new Uri("http://localhost:12345/mcp"),
            null,
            CancellationToken.None);

        IEnumerable<HttpRequestMessage> postInitRequests = handler.Captured
            .Where(c => c.Request.Method == HttpMethod.Post && c.Method != "initialize")
            .Select(c => c.Request);

        postInitRequests.Should().NotBeEmpty();
        postInitRequests.Should().AllSatisfy(r =>
        {
            r.Headers.TryGetValues("Mcp-Session-Id", out IEnumerable<string>? vals).Should().BeTrue();
            vals!.Should().ContainSingle(v => v == SessionId);
        });
    }

    [Fact]
    public async Task FetchResourceUrisAsync_WhenServerReturnsEmptyResources_ReturnsEmptyList()
    {
        var handler = new SequencedMcpHandler("session-004", []);
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var probe = new McpResourceProbe(() => httpClient);

        IReadOnlyList<string> uris = await probe.FetchResourceUrisAsync(
            new Uri("http://localhost:12345/mcp"),
            null,
            CancellationToken.None);

        uris.Should().BeEmpty();
    }

    [Fact]
    public void GetUserMessage_WithCancellationException_ReturnsCancelMessage()
    {
        string msg = McpResourceProbe.GetUserMessage(new OperationCanceledException("cancelled"));
        msg.Should().Contain("キャンセル");
    }

    [Theory]
    [InlineData("401 Unauthorized")]
    [InlineData("Unauthorized access")]
    [InlineData("Forbidden")]
    public void GetUserMessage_WithAuthError_ReturnsAuthMessage(string rawError)
    {
        string msg = McpResourceProbe.GetUserMessage(new InvalidOperationException(rawError));
        msg.Should().Contain("認証エラー");
    }

    [Theory]
    [InlineData("404 Not Found")]
    [InlineData("Endpoint Not Found")]
    public void GetUserMessage_WithNotFoundError_ReturnsNotFoundMessage(string rawError)
    {
        string msg = McpResourceProbe.GetUserMessage(new InvalidOperationException(rawError));
        msg.Should().Contain("404");
    }

    [Theory]
    [InlineData("Connection refused")]
    [InlineData("ECONNREFUSED")]
    public void GetUserMessage_WithConnectionRefused_ReturnsConnectionMessage(string rawError)
    {
        string msg = McpResourceProbe.GetUserMessage(new HttpRequestException(rawError));
        msg.Should().Contain("接続に失敗");
    }

    [Fact]
    public void GetUserMessage_WithUnknownError_ReturnsGenericMessage()
    {
        string msg = McpResourceProbe.GetUserMessage(new InvalidOperationException("something weird"));
        msg.Should().Contain("予期しないエラー");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Mock HTTP handler: responds with valid MCP Streamable HTTP protocol replies
    // and records each request with its parsed JSON-RPC method for assertions.
    // ─────────────────────────────────────────────────────────────────────────
    private sealed class SequencedMcpHandler : HttpMessageHandler
    {
        private readonly string _sessionId;
        private readonly IReadOnlyList<(string Uri, string Name)> _resources;
        private readonly List<(string Method, HttpRequestMessage Request)> _captured = [];

        public SequencedMcpHandler(string sessionId, IReadOnlyList<(string Uri, string Name)> resources)
        {
            _sessionId = sessionId;
            _resources = resources;
        }

        public IReadOnlyList<(string Method, HttpRequestMessage Request)> Captured => _captured;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content != null
                ? await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : "{}";

            string method = ExtractMethod(body);
            int id = ExtractId(body);

            _captured.Add((method, request));

            return method switch
            {
                "initialize" => CreateInitializeResponse(id),
                "notifications/initialized" => new HttpResponseMessage(HttpStatusCode.Accepted),
                "resources/list" => CreateResourcesListResponse(id),
                _ => new HttpResponseMessage(HttpStatusCode.NoContent),
            };
        }

        private static string ExtractMethod(string body)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("method", out JsonElement methodEl))
                {
                    return methodEl.GetString() ?? string.Empty;
                }
            }
            catch (JsonException)
            {
            }

            return string.Empty;
        }

        private static int ExtractId(string body)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("id", out JsonElement idEl) &&
                    idEl.ValueKind == JsonValueKind.Number)
                {
                    return idEl.GetInt32();
                }
            }
            catch (JsonException)
            {
            }

            return 0;
        }

        private HttpResponseMessage CreateInitializeResponse(int id)
        {
            string json =
                "{\"jsonrpc\":\"2.0\",\"id\":" + id + "," +
                "\"result\":{\"protocolVersion\":\"2024-11-05\"," +
                "\"capabilities\":{\"resources\":{}}," +
                "\"serverInfo\":{\"name\":\"test\",\"version\":\"1.0.0\"}}}";
            HttpResponseMessage response = new(HttpStatusCode.OK);
            response.Headers.Add("Mcp-Session-Id", _sessionId);
            response.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return response;
        }

        private HttpResponseMessage CreateResourcesListResponse(int id)
        {
            string resourcesJson = string.Join(
                ",",
                _resources.Select(r => "{\"uri\":\"" + r.Uri + "\",\"name\":\"" + r.Name + "\"}"));
            string json =
                "{\"jsonrpc\":\"2.0\",\"id\":" + id + "," +
                "\"result\":{\"resources\":[" + resourcesJson + "]}}";
            HttpResponseMessage response = new(HttpStatusCode.OK);
            response.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return response;
        }
    }
}
