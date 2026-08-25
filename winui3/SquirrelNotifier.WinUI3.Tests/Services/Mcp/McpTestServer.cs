// <copyright file="McpTestServer.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SquirrelNotifier.WinUI3.Tests.Services.Mcp;

/// <summary>
/// MCP テストの primary fixture。公式 SDK のサーバー実装をインメモリの TestServer で動かす。
/// </summary>
/// <remarks>
/// server/discover による negotiation、per-request metadata、SSE フレーミングといった protocol
/// lifecycle は SDK が担当する。テスト側が持つのは「どの resource を公開するか」だけであり、
/// SDK 更新時に fixture が protocol から乖離しない（#238）。
/// </remarks>
internal sealed class McpTestServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private McpTestServer(WebApplication app)
    {
        _app = app;
    }

    /// <summary>
    /// テスト用サーバーの MCP エンドポイント。TestServer はソケットを開かないためホスト名は任意.
    /// </summary>
    public static Uri Endpoint { get; } = new("http://localhost/mcp");

    /// <summary>
    /// 2026-07-28 対応のサーバーを起動する。session を持たない stateless モード.
    /// </summary>
    public static Task<McpTestServer> StartAsync(params TestResource[] resources)
    {
        return StartCoreAsync(HttpServerSessionMode.Stateless, protocolVersion: null, resources);
    }

    /// <summary>
    /// resources capability は広告するが、公開する resource が 1 件も無いサーバーを起動する.
    /// </summary>
    /// <remarks>
    /// 引数なしの <see cref="StartAsync"/> では resources capability そのものが広告されず、
    /// <c>resources/list</c> は <c>-32601</c> になる。「resource が空」を再現するには
    /// capability と handler を明示する必要がある。
    /// </remarks>
    public static async Task<McpTestServer> StartWithNoResourcesAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();

        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "squirrel-notifier-tests", Version = "1.0.0" };
                options.Capabilities = new ServerCapabilities { Resources = new ResourcesCapability() };
                options.Handlers.ListResourcesHandler = (_, _) =>
                    ValueTask.FromResult(new ListResourcesResult { Resources = [] });
            })
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless);

        WebApplication app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();

        return new McpTestServer(app);
    }

    /// <summary>
    /// 2026-07-28 に未対応の legacy サーバーを起動する。initialize handshake と
    /// <c>Mcp-Session-Id</c> を前提とする移行前の thread-owl / mcp-gateway 相当.
    /// </summary>
    public static Task<McpTestServer> StartLegacyOnlyAsync(params TestResource[] resources)
    {
        return StartCoreAsync(HttpServerSessionMode.Stateful, protocolVersion: "2025-11-25", resources);
    }

    /// <summary>
    /// 認証を要求し、bearer token が無いリクエストを 401 で拒否するサーバーを起動する.
    /// </summary>
    /// <remarks>
    /// mcp-gateway の OAuth 境界を模した構成。<c>server/discover</c> が 401 で弾かれると、
    /// SDK は「サーバーが initialize handshake を要求している」と解釈して protocol negotiation
    /// の失敗として報告するため、認証エラーと protocol 未対応が区別できなくなる。
    /// </remarks>
    public static async Task<McpTestServer> StartRequiringAuthorizationAsync(params TestResource[] resources)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();

        builder.Services
            .AddMcpServer(options =>
                options.ServerInfo = new Implementation { Name = "squirrel-notifier-tests", Version = "1.0.0" })
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithResources(resources.Select(CreateServerResource));

        WebApplication app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (!context.Request.Headers.ContainsKey("Authorization"))
            {
                // mcp-gateway が返す形に合わせる。WWW-Authenticate があると SDK は OAuth 経路を
                // 試みるため、単なる 401 とは異なる例外形になる。
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate =
                    "Bearer realm=\"mcp-gateway\", resource_metadata=\"http://localhost/.well-known/oauth-protected-resource/mcp\"";
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    "{\"error\":\"invalid_request\",\"error_description\":\"No access token provided.\"}");
                return;
            }

            await next(context);
        });
        app.MapMcp("/mcp");
        await app.StartAsync();

        return new McpTestServer(app);
    }

    /// <summary>
    /// このサーバーへ接続する <see cref="HttpMessageHandler"/> を、リクエスト記録付きで返す.
    /// </summary>
    public RecordingHttpHandler CreateRecordingHandler()
    {
        return new RecordingHttpHandler(_app.GetTestServer().CreateHandler());
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static async Task<McpTestServer> StartCoreAsync(
        HttpServerSessionMode sessionMode,
        string? protocolVersion,
        IReadOnlyList<TestResource> resources)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();

        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "squirrel-notifier-tests", Version = "1.0.0" };

                if (protocolVersion is not null)
                {
                    options.ProtocolVersion = protocolVersion;
                }
            })
            .WithHttpTransport(options => options.SessionMode = sessionMode)
            .WithResources(resources.Select(CreateServerResource));

        WebApplication app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();

        return new McpTestServer(app);
    }

    private static McpServerResource CreateServerResource(TestResource resource)
    {
        return McpServerResource.Create(
            () => resource.Text,
            new McpServerResourceCreateOptions
            {
                UriTemplate = resource.Uri,
                Name = resource.Name,
                MimeType = "application/json",
            });
    }
}

/// <summary>
/// テストサーバーが公開する resource の定義.
/// </summary>
internal sealed record TestResource(string Uri, string Name, string Text)
{
    public static TestResource ReviewQueue { get; } =
        new("queue://review/queue", "Review Queue", "{\"items\":[]}");

    public static TestResource ReReviewRequests { get; } =
        new("queue://review/re-review-requests", "Re-review Requests", "{\"items\":[]}");
}
