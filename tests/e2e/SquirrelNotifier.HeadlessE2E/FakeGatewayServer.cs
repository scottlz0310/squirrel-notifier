using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SquirrelNotifier.HeadlessE2E;

internal enum FakeGatewayMode
{
    Success,
    ProtocolMismatch,
    Unauthorized,
    ToolError,
    AuthenticationFlow,
}

internal sealed class FakeGatewayServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly FakeGatewayMode _mode;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentBag<Task> _handlers = [];
    private readonly ConcurrentQueue<FakeGatewayRequest> _requests = [];
    private readonly Task _acceptLoop;

    private FakeGatewayServer(TcpListener listener, FakeGatewayMode mode)
    {
        _listener = listener;
        _mode = mode;
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Endpoint = new Uri($"http://127.0.0.1:{port}/");
        _acceptLoop = AcceptLoopAsync();
    }

    public Uri Endpoint { get; }

    public IReadOnlyList<FakeGatewayRequest> Requests => [.. _requests];

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "listener の所有権は生成した FakeGatewayServer へ移譲する。")]
    public static Task<FakeGatewayServer> StartAsync(FakeGatewayMode mode)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return Task.FromResult(new FakeGatewayServer(listener, mode));
    }

    public static Uri CreateUnreachableEndpoint()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new Uri($"http://127.0.0.1:{port}/unreachable");
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync().ConfigureAwait(false);
        _listener.Stop();

        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await Task.WhenAll(_handlers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cancellation.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_cancellation.Token).ConfigureAwait(false);
                _handlers.Add(HandleClientAsync(client));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException) when (_cancellation.IsCancellationRequested)
        {
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using NetworkStream stream = client.GetStream();
                string headers = await ReadHeadersAsync(stream, _cancellation.Token).ConfigureAwait(false);
                string[] lines = headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                string[] requestLine = lines.Length == 0 ? [] : lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                string path = requestLine.Length >= 2 ? requestLine[1] : "/";
                string? authorization = lines
                    .FirstOrDefault(line => line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))?
                    .Split(':', 2)[1]
                    .Trim();
                bool hasAuthorization = authorization?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true;
                _requests.Enqueue(new FakeGatewayRequest(path, hasAuthorization));

                (int statusCode, string reason) = ResolveResponse(path, hasAuthorization);
                string body = statusCode == 200 ? "{\"result\":\"ok\"}" : "{\"error\":\"fixture\"}";
                string response = $"HTTP/1.1 {statusCode} {reason}\r\n"
                    + "Content-Type: application/json\r\n"
                    + (statusCode == 401 ? "WWW-Authenticate: Bearer realm=\"fixture\"\r\n" : string.Empty)
                    + $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n"
                    + "Connection: close\r\n\r\n"
                    + body;
                byte[] bytes = Encoding.UTF8.GetBytes(response);
                await stream.WriteAsync(bytes, _cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
        }
    }

    private (int StatusCode, string Reason) ResolveResponse(string path, bool hasAuthorization)
        => _mode switch
        {
            FakeGatewayMode.Success => (200, "OK"),
            FakeGatewayMode.ProtocolMismatch => (404, "Not Found"),
            FakeGatewayMode.Unauthorized => (401, "Unauthorized"),
            FakeGatewayMode.ToolError => (500, "Internal Server Error"),
            FakeGatewayMode.AuthenticationFlow when path.Contains("/login", StringComparison.OrdinalIgnoreCase)
                => (200, "OK"),
            FakeGatewayMode.AuthenticationFlow when hasAuthorization => (200, "OK"),
            FakeGatewayMode.AuthenticationFlow => (401, "Unauthorized"),
            _ => (500, "Internal Server Error"),
        };

    private static async Task<string> ReadHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[1024];
        while (buffer.Length < 16 * 1024)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            string text = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                return text;
            }
        }

        return Encoding.ASCII.GetString(buffer.ToArray());
    }
}

internal sealed record FakeGatewayRequest(string Path, bool HasAuthorization);
