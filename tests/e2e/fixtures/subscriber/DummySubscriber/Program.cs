using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SquirrelNotifier.E2EDummySubscriber;

[SuppressMessage(
    "Design",
    "CA1031:Do not catch general exception types",
    Justification = "fixture process は設定エラーを決定的な終了コードへ変換する必要がある。")]
[SuppressMessage(
    "Globalization",
    "CA1303:Do not pass literals as localized parameters",
    Justification = "subscriber fixture の stdout / stderr は決定的な wire fixture であり、翻訳対象ではない。")]
internal static class Program
{
    private const string _version = "v0.6.0";
    private const string _fixtureToken = "fixture-token-never-written-to-artifacts";
    private const string _observationPathVariable = "SQUIRREL_NOTIFIER_E2E_SUBSCRIBER_OBSERVATION_PATH";
    private const string _tokenCachePathVariable = "SQUIRREL_NOTIFIER_E2E_SUBSCRIBER_TOKEN_CACHE_PATH";
    private const string _flowVariable = "SQUIRREL_NOTIFIER_E2E_SUBSCRIBER_FLOW";
    private const string _preflightFailureVariable = "SQUIRREL_NOTIFIER_E2E_SUBSCRIBER_PREFLIGHT_FAILURE";
    private const string _delayVariable = "SQUIRREL_NOTIFIER_E2E_SUBSCRIBER_DELAY_MS";
    private const string _secondaryResourceUri = "queue://review/secondary";
    private const string _observationMutexName = "SquirrelNotifier.E2E.DummySubscriber.Observation";

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task<int> Main(string[] args)
    {
        try
        {
            return await RunAsync(args).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"subscriber fixture の設定または実行に失敗しました: {ex.Message}").ConfigureAwait(false);
            return 90;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        string mode = args.FirstOrDefault() ?? string.Empty;
        string? gatewayUrl = GetOption(args, "--url");
        string? resourceUri = GetOption(args, "--uri");

        if (string.Equals(mode, "--version", StringComparison.Ordinal))
        {
            Record(args, "version", null, null, false, 0);
            await WriteOutputAsync($"mcp-resource-subscriber {_version}").ConfigureAwait(false);
            return 0;
        }

        if (string.Equals(mode, "--help", StringComparison.Ordinal))
        {
            if (IsEnabled(_preflightFailureVariable))
            {
                Record(args, "help", null, null, false, 7);
                await Console.Error.WriteLineAsync("help preflight fixture failed.").ConfigureAwait(false);
                return 7;
            }

            Record(args, "help", null, null, false, 0);
            await WriteOutputAsync("mcp-resource-subscriber fixture: --version | --help | --login --url <url> | --url <url> --uri <uri> --timeout-ms <ms> --json").ConfigureAwait(false);
            return 0;
        }

        if (string.Equals(mode, "--login", StringComparison.Ordinal))
        {
            return await RunLoginAsync(args, gatewayUrl).ConfigureAwait(false);
        }

        if (string.Equals(mode, "call", StringComparison.Ordinal))
        {
            return await RunCallAsync(args, gatewayUrl).ConfigureAwait(false);
        }

        return await RunSubscriptionAsync(args, gatewayUrl, resourceUri).ConfigureAwait(false);
    }

    private static async Task<int> RunCallAsync(string[] args, string? gatewayUrl)
    {
        string? tool = GetOption(args, "--tool");
        string? toolArguments = GetOption(args, "--args");
        if (string.IsNullOrWhiteSpace(gatewayUrl)
            || !string.Equals(tool, "enqueue_review", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(toolArguments))
        {
            Record(args, "call", gatewayUrl, null, false, 2);
            await WriteOutputAsync("{\"isError\":true,\"content\":[{\"type\":\"text\",\"text\":\"CALL_ARGUMENTS_INVALID\"}]}").ConfigureAwait(false);
            return 2;
        }

        try
        {
            using HttpResponseMessage response = await SendRequestAsync(gatewayUrl, ReadTokenCache()).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Record(args, "call", gatewayUrl, null, false, 1);
                await WriteOutputAsync("{\"isError\":true,\"content\":[{\"type\":\"text\",\"text\":\"ENQUEUE_FAILED\"}]}").ConfigureAwait(false);
                return 1;
            }

            Record(args, "call", gatewayUrl, null, false, 0);
            await WriteOutputAsync("{\"isError\":false,\"content\":[]}").ConfigureAwait(false);
            return 0;
        }
        catch (HttpRequestException)
        {
            Record(args, "call", gatewayUrl, null, false, 3);
            await WriteOutputAsync("{\"isError\":true,\"content\":[{\"type\":\"text\",\"text\":\"CONNECTION_REFUSED\"}]}").ConfigureAwait(false);
            return 3;
        }
    }

    private static async Task<int> RunLoginAsync(string[] args, string? gatewayUrl)
    {
        if (string.IsNullOrWhiteSpace(gatewayUrl))
        {
            Record(args, "login", gatewayUrl, null, false, 2);
            await WriteOutputAsync("error-code SERVER_URL_UNKNOWN").ConfigureAwait(false);
            await WriteOutputAsync("login-status failed").ConfigureAwait(false);
            await Console.Error.WriteLineAsync("login failed: Gateway URL is missing.").ConfigureAwait(false);
            return 2;
        }

        HttpResponseMessage response;
        try
        {
            response = await SendRequestAsync(gatewayUrl, token: null).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            Record(args, "login", gatewayUrl, null, false, 1);
            await WriteOutputAsync("error-code CONNECTION_REFUSED").ConfigureAwait(false);
            await WriteOutputAsync("login-status failed").ConfigureAwait(false);
            await Console.Error.WriteLineAsync("login failed: Gateway is unreachable.").ConfigureAwait(false);
            return 1;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                Record(args, "login", gatewayUrl, null, false, 1);
                await WriteOutputAsync($"error-code {MapErrorCode(response.StatusCode)}").ConfigureAwait(false);
                await WriteOutputAsync("login-status failed").ConfigureAwait(false);
                await Console.Error.WriteLineAsync($"login failed: Gateway returned HTTP {(int)response.StatusCode}.").ConfigureAwait(false);
                return 1;
            }
        }

        Uri verificationUri = new(new Uri(gatewayUrl), "device");
        string userCode = "SQRL-1234";
        string completeUri = $"{verificationUri}?user_code={userCode}";

        await WriteOutputAsync($"user-code {userCode}").ConfigureAwait(false);
        await WriteOutputAsync($"verification-uri {verificationUri}").ConfigureAwait(false);
        await WriteOutputAsync($"verification-uri-complete {completeUri}").ConfigureAwait(false);
        WriteTokenCache();
        Record(args, "login", gatewayUrl, null, false, 0);
        await WriteOutputAsync("login-status success").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunSubscriptionAsync(string[] args, string? gatewayUrl, string? resourceUri)
    {
        bool hasJson = args.Contains("--json", StringComparer.Ordinal);
        string? timeout = GetOption(args, "--timeout-ms");
        if (string.IsNullOrWhiteSpace(gatewayUrl)
            || string.IsNullOrWhiteSpace(resourceUri)
            || !hasJson
            || string.IsNullOrWhiteSpace(timeout))
        {
            Record(args, "subscription", gatewayUrl, resourceUri, false, 2);
            await WriteOutputAsync(JsonSerializer.Serialize(new
            {
                route = "failed",
                errorCode = "CLI_ARGUMENT_MISSING",
                finalText = "Required subscriber arguments are missing.",
            }, _jsonOptions)).ConfigureAwait(false);
            return 2;
        }

        string? token = ReadTokenCache();
        bool hasAuthorization = !string.IsNullOrWhiteSpace(token);
        HttpResponseMessage response;
        try
        {
            response = await SendRequestAsync(gatewayUrl, token).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            Record(args, "subscription", gatewayUrl, resourceUri, hasAuthorization, 1);
            await WriteFailureAsync("CONNECTION_REFUSED", "Gateway is unreachable.").ConfigureAwait(false);
            return 1;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                string errorCode = MapErrorCode(response.StatusCode);
                Record(args, "subscription", gatewayUrl, resourceUri, hasAuthorization, 1);
                await WriteFailureAsync(errorCode, $"Gateway returned HTTP {(int)response.StatusCode}.").ConfigureAwait(false);
                return 1;
            }
        }

        await DelayIfConfiguredAsync().ConfigureAwait(false);

        string initialText = IsReviewEventFlow()
            ? BuildReviewEventFlowInitialText(resourceUri)
            : "[{\"owner\":\"fixture-owner\",\"repo\":\"fixture-repository\",\"prNumber\":307,\"reason\":\"initial-review\",\"queuedAt\":\"2026-01-01T00:00:00Z\",\"requestedBy\":\"fixture\"}]";
        string finalText = IsReviewEventFlow()
            ? BuildReviewEventFlowFinalText(resourceUri)
            : "[]";

        Record(args, "subscription", gatewayUrl, resourceUri, hasAuthorization, 0);
        await WriteOutputAsync(JsonSerializer.Serialize(new
        {
            route = "subscription",
            notificationReceived = true,
            initialText,
            finalText,
            serverUrl = gatewayUrl,
        }, _jsonOptions)).ConfigureAwait(false);
        return 0;
    }

    private static bool IsReviewEventFlow()
        => string.Equals(
            Environment.GetEnvironmentVariable(_flowVariable),
            "review-event-flow",
            StringComparison.OrdinalIgnoreCase);

    private static async Task DelayIfConfiguredAsync()
    {
        string? value = Environment.GetEnvironmentVariable(_delayVariable);
        if (int.TryParse(value, out int delayMs) && delayMs > 0)
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
        }
    }

    private static string BuildReviewEventFlowInitialText(string? resourceUri)
        => BuildReviewEventFlowText(resourceUri, includeNewCandidate: false);

    private static string BuildReviewEventFlowFinalText(string? resourceUri)
        => BuildReviewEventFlowText(resourceUri, includeNewCandidate: true);

    private static string BuildReviewEventFlowText(string? resourceUri, bool includeNewCandidate)
    {
        ReviewEventPayload[] payloads = string.Equals(resourceUri, _secondaryResourceUri, StringComparison.Ordinal)
            ? includeNewCandidate
                ?
                [
                    new ReviewEventPayload(
                        "secondary-owner",
                        "secondary-repository",
                        310,
                        "opened",
                        "2026-01-01T00:00:10Z",
                        "secondary-fixture"),
                    new ReviewEventPayload(
                        "fourth-owner",
                        "fourth-repository",
                        311,
                        "synchronized",
                        "2026-01-01T00:00:11Z",
                        "secondary-fixture"),
                ]
                :
                [
                    new ReviewEventPayload(
                        "secondary-owner",
                        "secondary-repository",
                        310,
                        "opened",
                        "2026-01-01T00:00:10Z",
                        "secondary-fixture"),
                ]
            : includeNewCandidate
                ?
                [
                    new ReviewEventPayload(
                        "fixture-owner",
                        "fixture-repository",
                        307,
                        "opened",
                        "2026-01-01T00:00:01Z",
                        "fixture"),
                    new ReviewEventPayload(
                        "second-owner",
                        "second-repository",
                        308,
                        "synchronized",
                        "2026-01-01T00:00:02Z",
                        "fixture"),
                    new ReviewEventPayload(
                        "third-owner",
                        "third-repository",
                        309,
                        "re-review-requested",
                        "2026-01-01T00:00:03Z",
                        "fixture"),
                ]
                :
                [
                    new ReviewEventPayload(
                        "fixture-owner",
                        "fixture-repository",
                        307,
                        "opened",
                        "2026-01-01T00:00:01Z",
                        "fixture"),
                    new ReviewEventPayload(
                        "second-owner",
                        "second-repository",
                        308,
                        "synchronized",
                        "2026-01-01T00:00:02Z",
                        "fixture"),
                ];

        return JsonSerializer.Serialize(payloads, _jsonOptions);
    }

    private static async Task<HttpResponseMessage> SendRequestAsync(string gatewayUrl, string? token)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3),
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, gatewayUrl);
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private static string MapErrorCode(HttpStatusCode statusCode)
        => statusCode switch
        {
            HttpStatusCode.Unauthorized => "AUTH_LOGIN_REQUIRED",
            HttpStatusCode.NotFound => "PROTOCOL_UNSUPPORTED",
            _ when (int)statusCode >= 500 => "MCP_TOOL_ERROR",
            _ => "SUBSCRIPTION_FAILED",
        };

    private static async Task WriteFailureAsync(string errorCode, string message)
    {
        await WriteOutputAsync(JsonSerializer.Serialize(new
        {
            route = "failed",
            errorCode,
            finalText = message,
        }, _jsonOptions)).ConfigureAwait(false);
    }

    private static Task WriteOutputAsync(string value)
        => Console.Out.WriteLineAsync(value);

    private static void WriteTokenCache()
    {
        string path = GetRequiredEnvironmentVariable(_tokenCachePathVariable);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { accessToken = _fixtureToken }, _jsonOptions));
    }

    private static string? ReadTokenCache()
    {
        string path = GetRequiredEnvironmentVariable(_tokenCachePathVariable);
        if (!File.Exists(path))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.TryGetProperty("accessToken", out JsonElement token)
            ? token.GetString()
            : null;
    }

    private static string? GetOption(string[] args, string option)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], option, StringComparison.Ordinal))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static bool IsEnabled(string name)
        => string.Equals(Environment.GetEnvironmentVariable(name), "1", StringComparison.Ordinal);

    private static void Record(
        string[] args,
        string mode,
        string? gatewayUrl,
        string? resourceUri,
        bool hasAuthorization,
        int exitCode)
    {
        string path = GetRequiredEnvironmentVariable(_observationPathVariable);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var observation = new
        {
            mode,
            arguments = args,
            gatewayUrl,
            resourceUri,
            hasAuthorization,
            exitCode,
        };
        using var mutex = new Mutex(false, _observationMutexName);
        bool acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                throw new IOException("subscriber observation の mutex を取得できませんでした。");
            }

            File.AppendAllText(path, JsonSerializer.Serialize(observation, _jsonOptions) + Environment.NewLine);
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static string GetRequiredEnvironmentVariable(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"環境変数 {name} が設定されていません。")
            : value;
    }

    private sealed record ReviewEventPayload(
        string Owner,
        string Repo,
        int PrNumber,
        string Reason,
        string QueuedAt,
        string RequestedBy);
}
