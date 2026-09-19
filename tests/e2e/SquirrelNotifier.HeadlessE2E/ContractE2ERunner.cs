using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.HeadlessE2E;

internal static class ContractE2ERunner
{
    private const string _subscriberObservationVariable = "SQUIRREL_NOTIFIER_E2E_SUBSCRIBER_OBSERVATION_PATH";
    private const string _subscriberTokenCacheVariable = "SQUIRREL_NOTIFIER_E2E_SUBSCRIBER_TOKEN_CACHE_PATH";
    private const string _subscriberFixtureVariable = "SQUIRREL_NOTIFIER_E2E_SUBSCRIBER_FIXTURE";
    private const string _authTokenVariable = "MCP_PROBE_AUTH_TOKEN";
    private const string _subscriberCommand = "mcp-resource-subscriber";
    private const string _resourceUri = "queue://review/queue";

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly Regex _guidRegex = new(
        @"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static async Task<IReadOnlyList<string>> RunAsync(
        string scenarioKind,
        string runRoot,
        string settingsDirectory,
        string logDirectory,
        string artifactsDirectory,
        string subscriberFixturePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(subscriberFixturePath))
        {
            throw new ContractE2EFailureException(
                "TEST_HARNESS_FAILED",
                "subscriber fixture の実行ファイルが見つかりません。");
        }

        string observationPath = Path.Combine(runRoot, "subscriber-invocations.jsonl");
        string tokenCachePath = Path.Combine(runRoot, "subscriber-token-cache", "token.json");
        var assertions = new List<string>();

        using var environment = new EnvironmentScope();
        environment.Set(_subscriberObservationVariable, observationPath);
        environment.Set(_subscriberTokenCacheVariable, tokenCachePath);
        environment.Set(_subscriberFixtureVariable, subscriberFixturePath);
        environment.Set(_authTokenVariable, null);

        try
        {
            switch (scenarioKind)
            {
                case "subscriber-gateway-contract":
                    await RunGatewayContractAsync(
                        settingsDirectory,
                        logDirectory,
                        assertions,
                        subscriberFixturePath,
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "gateway-auth-flow":
                    await RunAuthenticationFlowAsync(
                        runRoot,
                        settingsDirectory,
                        logDirectory,
                        assertions,
                        environment,
                        tokenCachePath,
                        cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new ContractE2EFailureException(
                        "TEST_HARNESS_FAILED",
                        $"未登録の契約 E2E scenario です: {scenarioKind}");
            }

            return assertions;
        }
        finally
        {
            WriteSubscriberArtifact(observationPath, artifactsDirectory);
        }
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "fake Gateway は case ごとの finally で明示的に破棄する。")]
    private static async Task RunGatewayContractAsync(
            string settingsDirectory,
            string logDirectory,
            List<string> assertions,
            string subscriberFixturePath,
            CancellationToken cancellationToken)
    {
        GatewayCase[] cases =
        [
            new("success", FakeGatewayMode.Success, null, false),
            new("protocol-mismatch", FakeGatewayMode.ProtocolMismatch, "プロトコル", false),
            new("unauthorized", FakeGatewayMode.Unauthorized, "認証", true),
            new("tool-error", FakeGatewayMode.ToolError, "予期しないエラー", false),
            new("unreachable", null, "接続", false),
        ];

        foreach (GatewayCase gatewayCase in cases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FakeGatewayServer? server = null;
            try
            {
                Uri endpoint;
                if (gatewayCase.Mode is FakeGatewayMode mode)
                {
                    server = await FakeGatewayServer.StartAsync(mode).ConfigureAwait(false);
                    endpoint = server.Endpoint;
                }
                else
                {
                    endpoint = FakeGatewayServer.CreateUnreachableEndpoint();
                }

                string caseSettingsDirectory = Path.Combine(settingsDirectory, gatewayCase.Id);
                SettingsService settings = CreateSettings(caseSettingsDirectory, subscriberFixturePath, endpoint.ToString());
                LoggingService logging = new(logDirectory);
                var notifications = new RecordingNotificationService();
                var service = new McpSubscriptionService(
                    settings,
                    notifications,
                    logging,
                    maxRetries: 0,
                    startTimeoutMs: 5000,
                    dependencyWaitBudgetMs: 0);

                try
                {
                    if (gatewayCase.Id == "success")
                    {
                        Task<ReviewEvent> reviewEventTask = notifications.WaitForReviewEventAsync(cancellationToken);
                        SubscriptionStartResult started = await service.StartAsync(cancellationToken).ConfigureAwait(false);
                        Check(started.Success, assertions, "success: subscriber preflight と購読開始が成功する");
                        ReviewEvent reviewEvent = await reviewEventTask.ConfigureAwait(false);
                        Check(
                            reviewEvent.Repository == "fixture-owner/fixture-repository" && reviewEvent.PrNumber == 307,
                            assertions,
                            "success: subscriber の InitialText から review event を復元する");
                        Check(
                            server?.Requests.Any(request => request.Path == "/" && !request.HasAuthorization) == true,
                            assertions,
                            "success: fake Gateway へ認証なしのローカル request を送る");
                        await service.StopAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        Task stateTask = WaitForStateAsync(service, SubscriptionState.Error, cancellationToken);
                        SubscriptionStartResult started = await service.StartAsync(cancellationToken).ConfigureAwait(false);
                        Check(started.Success, assertions, $"{gatewayCase.Id}: preflight 成功後に購読プロセスを起動する");
                        await stateTask.ConfigureAwait(false);
                        Check(service.State == SubscriptionState.Error, assertions, $"{gatewayCase.Id}: product が Error 状態へ遷移する");
                        Check(
                            gatewayCase.ExpectedError is null || service.LastError.Contains(gatewayCase.ExpectedError, StringComparison.Ordinal),
                            assertions,
                            $"{gatewayCase.Id}: Gateway 契約のエラー分類をユーザー向け状態へ反映する");
                        Check(
                            service.IsAuthenticationRequired == gatewayCase.RequiresAuthentication,
                            assertions,
                            $"{gatewayCase.Id}: 認証要求フラグを正しく分類する");
                        await service.StopAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    await service.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                if (server is not null)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        IReadOnlyList<SubscriberObservation> observations = ReadSubscriberObservations();
        Check(observations.Any(observation => observation.Mode == "help"), assertions, "契約 E2E: --help preflight を実プロセスで検証する");
        IReadOnlyList<SubscriberObservation> subscriptions = observations
            .Where(observation => observation.Mode == "subscription")
            .ToArray();
        Check(subscriptions.Count > 0, assertions, "契約 E2E: subscriber の購読モードを実プロセスで起動する");
        Check(
            subscriptions.All(observation => ContainsOption(observation.Arguments, "--url")
                && ContainsOption(observation.Arguments, "--uri")
                && ContainsOption(observation.Arguments, "--timeout-ms")
                && observation.Arguments.Contains("--json", StringComparer.Ordinal)),
            assertions,
            "契約 E2E: subscriber へ --url / --uri / --timeout-ms / --json を渡す");
    }

    [SuppressMessage(
        "Reliability",
        "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "await using の初期化で作成した fake Gateway はメソッド終了時に確実に破棄する。")]
    private static async Task RunAuthenticationFlowAsync(
            string runRoot,
            string settingsDirectory,
            string logDirectory,
            List<string> assertions,
            EnvironmentScope environment,
            string tokenCachePath,
            CancellationToken cancellationToken)
    {
        string shimDirectory = Path.Combine(runRoot, "subscriber-shims");
        Directory.CreateDirectory(shimDirectory);
        CreateSubscriberShim(shimDirectory, ".cmd");
        CreateSubscriberShim(shimDirectory, ".bat");
        string currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        environment.Set("PATH", string.Join(Path.PathSeparator, shimDirectory, currentPath));
        environment.Set("PATHEXT", ".CMD;.BAT;.EXE");

        string? resolvedCmd = CommandPathResolver.Resolve(_subscriberCommand);
        Check(resolvedCmd?.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) == true, assertions, "PATH / PATHEXT: .cmd shim を優先順で解決する");

        await using FakeGatewayServer server = await FakeGatewayServer.StartAsync(FakeGatewayMode.AuthenticationFlow).ConfigureAwait(false);
        Uri loginEndpoint = new(server.Endpoint, "login");
        Uri subscribeEndpoint = new(server.Endpoint, "subscribe");
        SettingsService settings = CreateSettings(settingsDirectory, _subscriberCommand, subscribeEndpoint.ToString());
        LoggingService logging = new(logDirectory);

        await RunSubscriptionAsync(
            settings,
            logging,
            assertions,
            expectedSuccess: false,
            expectedAuthentication: true,
            label: "認証前",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Check(
            server.Requests.Any(request => request.Path == "/subscribe" && !request.HasAuthorization),
            assertions,
            "認証前: token cache 未作成では Authorization なしで Gateway に到達する");

        environment.Set("PATHEXT", ".BAT;.CMD;.EXE");
        string? resolvedBat = CommandPathResolver.Resolve(_subscriberCommand);
        Check(resolvedBat?.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) == true, assertions, "PATH / PATHEXT: .bat shim を優先順で解決する");
        settings.UpdateSettings(
            _subscriberCommand,
            string.Empty,
            loginEndpoint.ToString(),
            [_resourceUri],
            1000,
            "fixture-launcher",
            string.Empty,
            string.Empty,
            "fixture-launcher",
            string.Empty,
            string.Empty,
            1000,
            false,
            LauncherAgentCatalog.CustomPresetId,
            LauncherAgentCatalog.CustomPresetId);

        var urlOpener = new RecordingUrlOpener(result: false);
        bool verificationReceived = false;
        bool browserOpened = true;
        var loginService = new McpLoginService(settings, logging, urlOpener: urlOpener, loginTimeoutMs: 5000);
        loginService.VerificationReceived += (_, info) =>
        {
            verificationReceived = true;
            browserOpened = info.BrowserOpened;
        };
        McpLoginResult loginResult = await loginService.LoginAsync(cancellationToken).ConfigureAwait(false);

        Check(loginResult.Success, assertions, "device flow: subscriber の --version と --login が成功する");
        Check(File.Exists(tokenCachePath), assertions, "device flow: 認証成功後に token cache が作成される");
        Check(
            verificationReceived && !browserOpened && urlOpener.OpenedUrls.Count == 1,
            assertions,
            "device flow: browser 起動失敗時も verification URL と code の導線を受け取る");
        Check(
            server.Requests.Any(request => request.Path == "/login" && !request.HasAuthorization),
            assertions,
            "device flow: login request は未認証の fake Gateway へ送る");

        environment.Set("PATHEXT", ".CMD;.BAT;.EXE");
        settings.UpdateSettings(
            _subscriberCommand,
            string.Empty,
            subscribeEndpoint.ToString(),
            [_resourceUri],
            1000,
            "fixture-launcher",
            string.Empty,
            string.Empty,
            "fixture-launcher",
            string.Empty,
            string.Empty,
            1000,
            false,
            LauncherAgentCatalog.CustomPresetId,
            LauncherAgentCatalog.CustomPresetId);

        await RunSubscriptionAsync(
            settings,
            logging,
            assertions,
            expectedSuccess: true,
            expectedAuthentication: false,
            label: "認証後",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Check(
            server.Requests.Any(request => request.Path == "/subscribe" && request.HasAuthorization),
            assertions,
            "認証後: token cache を使って Authorization 付きで再購読する");

        string log = File.Exists(Path.Combine(logDirectory, "winui3.log"))
            ? await File.ReadAllTextAsync(Path.Combine(logDirectory, "winui3.log"), cancellationToken).ConfigureAwait(false)
            : string.Empty;
        string settingsJson = File.Exists(Path.Combine(settingsDirectory, "settings.json"))
            ? await File.ReadAllTextAsync(Path.Combine(settingsDirectory, "settings.json"), cancellationToken).ConfigureAwait(false)
            : string.Empty;
        Check(!log.Contains("fixture-token", StringComparison.Ordinal), assertions, "secret 非露出: product log に token marker を出力しない");
        Check(!settingsJson.Contains("fixture-token", StringComparison.Ordinal), assertions, "secret 非露出: settings.json に token marker を保存しない");

        List<SubscriberObservation> observations = ReadSubscriberObservations();
        Check(
            observations.Any(observation => observation.Mode == "version" && observation.Arguments.SequenceEqual(["--version"])),
            assertions,
            "CLI version 契約: --login 前に --version を確認する");
        Check(
            observations.Any(observation => observation.Mode == "login" && ContainsOption(observation.Arguments, "--login") && ContainsOption(observation.Arguments, "--url")),
            assertions,
            "CLI login 契約: --login と --url を渡す");
    }

    private static async Task RunSubscriptionAsync(
        SettingsService settings,
        LoggingService logging,
        List<string> assertions,
        bool expectedSuccess,
        bool expectedAuthentication,
        string label,
        CancellationToken cancellationToken)
    {
        var notifications = new RecordingNotificationService();
        var service = new McpSubscriptionService(
            settings,
            notifications,
            logging,
            maxRetries: 0,
            startTimeoutMs: 5000,
            dependencyWaitBudgetMs: 0);

        try
        {
            if (expectedSuccess)
            {
                Task<ReviewEvent> reviewEventTask = notifications.WaitForReviewEventAsync(cancellationToken);
                SubscriptionStartResult started = await service.StartAsync(cancellationToken).ConfigureAwait(false);
                Check(started.Success, assertions, $"{label}: subscriber preflight と購読開始が成功する");
                ReviewEvent reviewEvent = await reviewEventTask.ConfigureAwait(false);
                Check(reviewEvent.PrNumber == 307, assertions, $"{label}: 認証後の再購読で review event を受信する");
                Check(!service.IsAuthenticationRequired, assertions, $"{label}: 認証後は認証要求状態にならない");
                await service.StopAsync().ConfigureAwait(false);
            }
            else
            {
                Task stateTask = WaitForStateAsync(service, SubscriptionState.Error, cancellationToken);
                SubscriptionStartResult started = await service.StartAsync(cancellationToken).ConfigureAwait(false);
                Check(started.Success, assertions, $"{label}: preflight 成功後に購読プロセスを起動する");
                await stateTask.ConfigureAwait(false);
                Check(service.State == SubscriptionState.Error, assertions, $"{label}: 認証失敗を Error 状態へ反映する");
                Check(service.IsAuthenticationRequired == expectedAuthentication, assertions, $"{label}: 認証要求フラグを正しく反映する");
                await service.StopAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await service.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static SettingsService CreateSettings(string directory, string commandPath, string gatewayUrl)
    {
        var settings = new SettingsService(directory, string.Empty);
        settings.UpdateSettings(
            commandPath,
            string.Empty,
            gatewayUrl,
            [_resourceUri],
            1000,
            "fixture-launcher",
            string.Empty,
            string.Empty,
            "fixture-launcher",
            string.Empty,
            string.Empty,
            1000,
            false,
            LauncherAgentCatalog.CustomPresetId,
            LauncherAgentCatalog.CustomPresetId);
        return settings;
    }

    private static async Task WaitForStateAsync(
        McpSubscriptionService service,
        SubscriptionState expected,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnStateChanged(object? sender, SubscriptionState state)
        {
            if (state == expected)
            {
                _ = completion.TrySetResult();
            }
        }

        service.StateChanged += OnStateChanged;
        try
        {
            if (service.State == expected)
            {
                return;
            }

            await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            service.StateChanged -= OnStateChanged;
        }
    }

    private static string CreateSubscriberShim(string directory, string extension)
    {
        string path = Path.Combine(directory, _subscriberCommand + extension);
        File.WriteAllText(
            path,
            "@echo off\r\n\"%SQUIRREL_NOTIFIER_E2E_SUBSCRIBER_FIXTURE%\" %*\r\n",
            Encoding.ASCII);
        return path;
    }

    private static List<SubscriberObservation> ReadSubscriberObservations()
    {
        string path = Environment.GetEnvironmentVariable(_subscriberObservationVariable)
            ?? throw new ContractE2EFailureException("TEST_HARNESS_FAILED", "subscriber observation path がありません。");
        if (!File.Exists(path))
        {
            throw new ContractE2EFailureException("TEST_HARNESS_FAILED", "subscriber の invocation record がありません。");
        }

        var observations = new List<SubscriberObservation>();
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            SubscriberObservation? observation = JsonSerializer.Deserialize<SubscriberObservation>(line, _jsonOptions);
            if (observation is null)
            {
                throw new ContractE2EFailureException("TEST_HARNESS_FAILED", "subscriber invocation record を解釈できません。");
            }

            observations.Add(observation);
        }

        return observations;
    }

    private static void WriteSubscriberArtifact(string observationPath, string artifactsDirectory)
    {
        Directory.CreateDirectory(artifactsDirectory);
        IReadOnlyList<SubscriberObservation> observations = File.Exists(observationPath)
            ? ReadSubscriberObservations()
            : [];
        string json = JsonSerializer.Serialize(observations, _jsonOptions);
        json = _guidRegex.Replace(json, "<redacted-session-id>");
        File.WriteAllText(Path.Combine(artifactsDirectory, "subscriber-invocations.json"), json);
    }

    private static bool ContainsOption(string[] arguments, string option)
        => arguments.Contains(option, StringComparer.Ordinal);

    private static void Check(bool condition, List<string> assertions, string description)
    {
        if (!condition)
        {
            throw new ContractE2EFailureException("PRODUCT_CONTRACT_MISMATCH", description);
        }

        assertions.Add(description);
    }

    private sealed record GatewayCase(
        string Id,
        FakeGatewayMode? Mode,
        string? ExpectedError,
        bool RequiresAuthentication);

    private sealed class RecordingNotificationService : INotificationService
    {
        private readonly TaskCompletionSource<ReviewEvent> _reviewEvent = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<ReviewEvent>? ReviewEventReceived;

        public event EventHandler<NotificationMessage>? NotificationRequested;

        public void NotifyReviewEvent(ReviewEvent reviewEvent)
        {
            _ = _reviewEvent.TrySetResult(reviewEvent);
            ReviewEventReceived?.Invoke(this, reviewEvent);
        }

        public void NotifyRateLimitReset(string label)
        {
            NotificationRequested?.Invoke(this, new NotificationMessage("fixture", label));
        }

        public Task<ReviewEvent> WaitForReviewEventAsync(CancellationToken cancellationToken)
            => _reviewEvent.Task.WaitAsync(cancellationToken);
    }

    private sealed class RecordingUrlOpener : IUrlOpener
    {
        private readonly bool _result;

        public RecordingUrlOpener(bool result)
        {
            _result = result;
        }

        public List<string> OpenedUrls { get; } = [];

        public bool TryOpen(string url)
        {
            OpenedUrls.Add(url);
            return _result;
        }
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _originalValues = new(StringComparer.Ordinal);
        private bool _disposed;

        public void Set(string name, string? value)
        {
            if (!_originalValues.ContainsKey(name))
            {
                _originalValues[name] = Environment.GetEnvironmentVariable(name);
            }

            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            foreach ((string name, string? value) in _originalValues)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            _disposed = true;
        }
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "JSON serializer が subscriber observation を生成する。")]
    private sealed class SubscriberObservation
    {
        public string Mode { get; set; } = string.Empty;

        public string[] Arguments { get; set; } = [];

        public string? GatewayUrl { get; set; }

        public string? ResourceUri { get; set; }

        public bool HasAuthorization { get; set; }

        public int ExitCode { get; set; }
    }
}

[SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification = "failure category を必須にする runner 内部例外である。")]
internal sealed class ContractE2EFailureException : Exception
{
    public ContractE2EFailureException(string category, string message)
        : base(message)
    {
        Category = category;
    }

    public string Category { get; }
}
