using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.HeadlessE2E;

[SuppressMessage(
    "Design",
    "CA1031:Do not catch general exception types",
    Justification = "fixture observation の不正は E2E harness の失敗として扱う。")]
internal static class ReviewEventFlowE2ERunner
{
    private const string _subscriberObservationVariable = "SQUIRREL_NOTIFIER_E2E_SUBSCRIBER_OBSERVATION_PATH";
    private const string _tokenCachePathVariable = "SQUIRREL_NOTIFIER_E2E_SUBSCRIBER_TOKEN_CACHE_PATH";
    private const string _flowVariable = "SQUIRREL_NOTIFIER_E2E_SUBSCRIBER_FLOW";
    private const string _preflightFailureVariable = "SQUIRREL_NOTIFIER_E2E_SUBSCRIBER_PREFLIGHT_FAILURE";
    private const string _delayVariable = "SQUIRREL_NOTIFIER_E2E_SUBSCRIBER_DELAY_MS";
    private const string _observationMutexName = "SquirrelNotifier.E2E.DummySubscriber.Observation";
    private const string _resourceUri = "queue://review/queue";
    private const string _secondaryResourceUri = "queue://review/secondary";
    private const int _pullRequestNumber = 307;
    private const int _expectedEventCount = 5;

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task<IReadOnlyList<string>> RunAsync(
        string runRoot,
        string settingsDirectory,
        string logDirectory,
        string artifactsDirectory,
        string dummyLauncherPath,
        string subscriberFixturePath,
        CancellationToken cancellationToken)
    {
        string observationPath = Path.Combine(runRoot, "subscriber-invocations.jsonl");
        var assertions = new List<string>();

        using var environment = new EnvironmentScope();
        environment.Set(_subscriberObservationVariable, observationPath);
        environment.Set(_tokenCachePathVariable, Path.Combine(runRoot, "subscriber-token-cache.json"));
        environment.Set(_flowVariable, "review-event-flow");
        environment.Set(_preflightFailureVariable, null);
        environment.Set(_delayVariable, "100");

        try
        {
            FakeGatewayServer server = await FakeGatewayServer
                .StartAsync(FakeGatewayMode.Success)
                .ConfigureAwait(false);

            try
            {
                await RunRegistrationCaseAsync(
                    "stopped",
                    induceInitialError: false,
                    startAlreadyRunning: false,
                    server.Endpoint.ToString(),
                        runRoot,
                        settingsDirectory,
                        logDirectory,
                        dummyLauncherPath,
                        subscriberFixturePath,
                        environment,
                        assertions,
                        cancellationToken).ConfigureAwait(false);

                await RunRegistrationCaseAsync(
                    "error",
                    induceInitialError: true,
                    startAlreadyRunning: false,
                    server.Endpoint.ToString(),
                        runRoot,
                        settingsDirectory,
                        logDirectory,
                        dummyLauncherPath,
                        subscriberFixturePath,
                        environment,
                    assertions,
                    cancellationToken).ConfigureAwait(false);

                await RunRegistrationCaseAsync(
                    "running",
                    induceInitialError: false,
                    startAlreadyRunning: true,
                    server.Endpoint.ToString(),
                    runRoot,
                    settingsDirectory,
                    logDirectory,
                    dummyLauncherPath,
                    subscriberFixturePath,
                    environment,
                    assertions,
                    cancellationToken).ConfigureAwait(false);

                return assertions;
            }
            finally
            {
                await server.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            WriteSubscriberArtifact(observationPath, artifactsDirectory);
        }
    }

    private static async Task RunRegistrationCaseAsync(
        string caseId,
        bool induceInitialError,
        bool startAlreadyRunning,
        string gatewayUrl,
        string runRoot,
        string settingsDirectory,
        string logDirectory,
        string dummyLauncherPath,
        string subscriberFixturePath,
        EnvironmentScope environment,
        List<string> assertions,
        CancellationToken cancellationToken)
    {
        string caseSettingsDirectory = Path.Combine(settingsDirectory, $"registration-{caseId}");
        string caseLogDirectory = Path.Combine(logDirectory, $"registration-{caseId}");
        SettingsService settings = CreateSettings(
            caseSettingsDirectory,
            subscriberFixturePath,
            gatewayUrl,
            dummyLauncherPath);
        var logging = new LoggingService(caseLogDirectory);
        var notifications = new RecordingNotificationService(_expectedEventCount);
        var subscription = new McpSubscriptionService(
            settings,
            notifications,
            logging,
            maxRetries: 0,
            startTimeoutMs: 5000,
            dependencyWaitBudgetMs: 0);
        int subscriberObservationStart = ReadSubscriberObservations().Count;

        try
        {
            if (startAlreadyRunning)
            {
                SubscriptionStartResult initialStart = await subscription
                    .StartAsync(cancellationToken)
                    .ConfigureAwait(false);
                Check(
                    initialStart.Success && subscription.State == SubscriptionState.Running,
                    assertions,
                    "Running 状態の購読から enqueue を開始する");
            }
            else if (induceInitialError)
            {
                environment.Set(_preflightFailureVariable, "1");
                try
                {
                    SubscriptionStartResult initialStart = await subscription
                        .StartAsync(cancellationToken)
                        .ConfigureAwait(false);
                    Check(
                        !initialStart.Success && subscription.State == SubscriptionState.Error,
                        assertions,
                        "Error 状態の購読を実プロセスの preflight 失敗で再現する");

                    IReadOnlyList<SubscriberObservation> failedStartObservations = ReadSubscriberObservations()
                        .Skip(subscriberObservationStart)
                        .ToArray();
                    Check(
                        failedStartObservations.All(
                            observation => observation.Mode is not ("version" or "call" or "subscription")),
                        assertions,
                        "購読開始に失敗した場合は enqueue を呼び出さない");
                }
                finally
                {
                    environment.Set(_preflightFailureVariable, null);
                }
            }
            else
            {
                Check(
                    subscription.State == SubscriptionState.Stopped,
                    assertions,
                    "Stopped 状態の購読からレビュー登録を開始する");
            }

            bool confirmationRequested = false;
            var registration = new ReviewRegistrationService(
                subscription,
                new EnqueueReviewService(settings, logging));
            ReviewRegistrationResult registrationResult = await registration.RegisterAsync(
                new PrReference("fixture-owner", "fixture-repository", _pullRequestNumber),
                "opened",
                _ =>
                {
                    confirmationRequested = true;
                    return Task.FromResult(true);
                },
                cancellationToken).ConfigureAwait(false);

            Check(
                registrationResult.Outcome == ReviewRegistrationOutcome.Registered,
                assertions,
                $"{caseId}: 購読開始後に enqueue_review が成功する");
            Check(
                confirmationRequested == !startAlreadyRunning,
                assertions,
                $"{caseId}: Stopped / Error の購読開始前に確認を要求する");
            Check(
                subscription.State == SubscriptionState.Running,
                assertions,
                $"{caseId}: enqueue 前に購読が Running へ到達する");

            IReadOnlyList<ReviewEvent> reviewEvents = await notifications
                .WaitForEventsAsync(cancellationToken)
                .ConfigureAwait(false);
            AssertReviewEvents(reviewEvents, caseId, assertions);

            IReadOnlyList<SubscriberObservation> observationsBeforeStop = ReadSubscriberObservations()
                .Skip(subscriberObservationStart)
                .ToArray();
            AssertEnqueueAfterSubscription(observationsBeforeStop, caseId, assertions);
            AssertPreflightBehavior(observationsBeforeStop, caseId, induceInitialError, assertions);

            await subscription.StopAsync().ConfigureAwait(false);

            int launcherObservationStart = ReadLauncherObservations(runRoot).Count;
            foreach (ReviewEvent reviewEvent in reviewEvents)
            {
                var launcher = new ReviewLauncherService(settings, logging);
                LauncherResult launchResult = await launcher
                    .LaunchAsync(reviewEvent, LauncherRole.Reviewer, cancellationToken)
                    .ConfigureAwait(false);
                Check(
                    launchResult.Success,
                    assertions,
                    $"{caseId}: 通知モデルから dummy launcher を起動する ({reviewEvent.Repository}#{reviewEvent.PrNumber})");
            }

            IReadOnlyList<LauncherObservation> launcherObservations = ReadLauncherObservations(runRoot)
                .Skip(launcherObservationStart)
                .ToArray();
            Check(
                launcherObservations.Count == _expectedEventCount,
                assertions,
                $"{caseId}: 各通知イベントを一度ずつ dummy launcher へ渡す");

            for (int index = 0; index < reviewEvents.Count; index++)
            {
                ReviewEvent reviewEvent = reviewEvents[index];
                LauncherObservation observation = launcherObservations[index];
                string[] expectedArguments =
                [
                    "--repository",
                    reviewEvent.Repository,
                    "--pull-request",
                    reviewEvent.PrNumber.ToString(CultureInfo.InvariantCulture),
                    "--reason",
                    reviewEvent.Reason,
                ];
                Check(
                    observation.Arguments.SequenceEqual(expectedArguments),
                    assertions,
                    $"{caseId}: owner/repo・PR 番号・reason を launcher 引数へ展開する ({reviewEvent.PrNumber})");
                Check(
                    observation.WorkingDirectory.Contains(
                        Path.Combine("launcher-workspace", "reviewer"),
                        StringComparison.OrdinalIgnoreCase),
                    assertions,
                    $"{caseId}: reviewer launcher の working directory を隔離する ({reviewEvent.PrNumber})");
            }
        }
        finally
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static SettingsService CreateSettings(
        string settingsDirectory,
        string subscriberFixturePath,
        string gatewayUrl,
        string dummyLauncherPath)
    {
        const string launcherArguments = "--repository {owner}/{repo} --pull-request {prNumber} --reason {reason}";
        var settings = new SettingsService(settingsDirectory, string.Empty);
        settings.UpdateSettings(
            subscriberFixturePath,
            string.Empty,
            gatewayUrl,
            [_resourceUri, _secondaryResourceUri],
            30000,
            dummyLauncherPath,
            launcherArguments,
            string.Empty,
            dummyLauncherPath,
            launcherArguments,
            string.Empty,
            30000,
            false,
            LauncherAgentCatalog.CustomPresetId,
            LauncherAgentCatalog.CustomPresetId);
        return settings;
    }

    private static void AssertReviewEvents(
        IReadOnlyList<ReviewEvent> reviewEvents,
        string caseId,
        List<string> assertions)
    {
        Check(
            reviewEvents.Count == _expectedEventCount,
            assertions,
            $"{caseId}: InitialText / FinalText の重複を除いて通知モデルを 5 件へ集約する");

        HashSet<string> expected =
        [
            $"{_resourceUri}|fixture-owner/fixture-repository#307:opened",
            $"{_resourceUri}|second-owner/second-repository#308:synchronized",
            $"{_resourceUri}|third-owner/third-repository#309:re-review-requested",
            $"{_secondaryResourceUri}|secondary-owner/secondary-repository#310:opened",
            $"{_secondaryResourceUri}|fourth-owner/fourth-repository#311:synchronized",
        ];
        HashSet<string> actual = reviewEvents
            .Select(reviewEvent => $"{reviewEvent.Source}|{reviewEvent.Repository}#{reviewEvent.PrNumber}:{reviewEvent.Reason}")
            .ToHashSet(StringComparer.Ordinal);
        Check(
            actual.SetEquals(expected),
            assertions,
            $"{caseId}: 複数 PR と reason を混線させずに parse する");
        Check(
            reviewEvents.Count(reviewEvent => reviewEvent.Source == _resourceUri) == 3
                && reviewEvents.Count(reviewEvent => reviewEvent.Source == _secondaryResourceUri) == 2,
            assertions,
            $"{caseId}: 複数 URI の通知を source ごとに分離する");
    }

    private static void AssertEnqueueAfterSubscription(
        IReadOnlyList<SubscriberObservation> observations,
        string caseId,
        List<string> assertions)
    {
        int subscriptionIndex = -1;
        int callIndex = -1;
        int helpIndex = -1;
        for (int index = 0; index < observations.Count; index++)
        {
            if (helpIndex < 0 && observations[index].Mode == "help")
            {
                helpIndex = index;
            }

            if (subscriptionIndex < 0 && observations[index].Mode == "subscription")
            {
                subscriptionIndex = index;
            }

            if (callIndex < 0 && observations[index].Mode == "call")
            {
                callIndex = index;
            }
        }
        SubscriberObservation? call = observations.FirstOrDefault(observation => observation.Mode == "call");

        Check(
            subscriptionIndex >= 0,
            assertions,
            $"{caseId}: Running 到達後に subscriber の購読プロセスを起動する");
        HashSet<string> subscriptionUris = observations
            .Where(observation => observation.Mode == "subscription" && observation.ResourceUri is not null)
            .Select(observation => observation.ResourceUri!)
            .ToHashSet(StringComparer.Ordinal);
        Check(
            subscriptionUris.SetEquals([_resourceUri, _secondaryResourceUri]),
            assertions,
            $"{caseId}: 複数 URI の購読プロセスを混線させずに起動する");
        Check(
            helpIndex >= 0 && callIndex > helpIndex,
            assertions,
            $"{caseId}: preflight を通過した後で enqueue を呼び出す");
        Check(
            call is not null
                && call.Arguments.Contains("call", StringComparer.Ordinal)
                && call.Arguments.Contains("--tool", StringComparer.Ordinal)
                && call.Arguments.Contains("enqueue_review", StringComparer.Ordinal),
            assertions,
            $"{caseId}: enqueue_review を subscriber の call 境界へ渡す");

        string? argsJson = GetOptionValue(call?.Arguments ?? [], "--args");
        bool targetMatches = false;
        if (argsJson is not null)
        {
            using JsonDocument document = JsonDocument.Parse(argsJson);
            JsonElement root = document.RootElement;
            targetMatches = root.GetProperty("owner").GetString() == "fixture-owner"
                && root.GetProperty("repo").GetString() == "fixture-repository"
                && root.GetProperty("prNumber").GetInt32() == _pullRequestNumber
                && root.GetProperty("reason").GetString() == "opened";
        }

        Check(
            targetMatches,
            assertions,
            $"{caseId}: enqueue_review の owner/repo/PR/reason を維持する");
    }

    private static void AssertPreflightBehavior(
        IReadOnlyList<SubscriberObservation> observations,
        string caseId,
        bool inducedInitialError,
        List<string> assertions)
    {
        int failedPreflightCount = observations.Count(
            observation => observation.Mode == "help" && observation.ExitCode == 7);
        Check(
            failedPreflightCount == (inducedInitialError ? 1 : 0),
            assertions,
            $"{caseId}: preflight 失敗時に retry せず、必要な開始だけを実行する");
    }

    private static string? GetOptionValue(string[] arguments, string option)
    {
        for (int index = 0; index < arguments.Length - 1; index++)
        {
            if (arguments[index] == option)
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

    private static List<SubscriberObservation> ReadSubscriberObservations()
    {
        string path = Environment.GetEnvironmentVariable(_subscriberObservationVariable)
            ?? throw new ContractE2EFailureException("TEST_HARNESS_FAILED", "subscriber observation path がありません。");
        if (!File.Exists(path))
        {
            return [];
        }

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
                throw new ContractE2EFailureException("TEST_HARNESS_FAILED", "subscriber observation の mutex を取得できませんでした。");
            }

            var observations = new List<SubscriberObservation>();
            foreach (string line in File.ReadLines(path))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    SubscriberObservation? observation = JsonSerializer.Deserialize<SubscriberObservation>(line, _jsonOptions);
                    if (observation is null)
                    {
                        throw new ContractE2EFailureException("TEST_HARNESS_FAILED", "subscriber invocation record を解釈できません。");
                    }

                    observations.Add(observation);
                }
            }

            return observations;
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static List<LauncherObservation> ReadLauncherObservations(string runRoot)
    {
        string path = Path.Combine(runRoot, "dummy-invocations.jsonl");
        if (!File.Exists(path))
        {
            return [];
        }

        var observations = new List<LauncherObservation>();
        foreach (string line in File.ReadLines(path))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                LauncherObservation? observation = JsonSerializer.Deserialize<LauncherObservation>(line, _jsonOptions);
                if (observation is null)
                {
                    throw new ContractE2EFailureException("TEST_HARNESS_FAILED", "dummy launcher invocation record を解釈できません。");
                }

                observations.Add(observation);
            }
        }

        return observations;
    }

    private static void WriteSubscriberArtifact(string observationPath, string artifactsDirectory)
    {
        Directory.CreateDirectory(artifactsDirectory);
        IReadOnlyList<SubscriberObservation> observations = File.Exists(observationPath)
            ? ReadSubscriberObservations()
            : [];
        File.WriteAllText(
            Path.Combine(artifactsDirectory, "subscriber-invocations.json"),
            JsonSerializer.Serialize(observations, _jsonOptions));
    }

    private static void Check(bool condition, List<string> assertions, string description)
    {
        if (!condition)
        {
            throw new ContractE2EFailureException("PRODUCT_CONTRACT_MISMATCH", description);
        }

        assertions.Add(description);
    }

    private sealed class RecordingNotificationService : INotificationService
    {
        private readonly int _expectedCount;
        private readonly List<ReviewEvent> _events = [];
        private readonly TaskCompletionSource<IReadOnlyList<ReviewEvent>> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _lock = new();

        public RecordingNotificationService(int expectedCount)
        {
            _expectedCount = expectedCount;
        }

        public event EventHandler<ReviewEvent>? ReviewEventReceived;

        public event EventHandler<NotificationMessage>? NotificationRequested;

        public void NotifyReviewEvent(ReviewEvent reviewEvent)
        {
            IReadOnlyList<ReviewEvent>? snapshot = null;
            lock (_lock)
            {
                _events.Add(reviewEvent);
                if (_events.Count >= _expectedCount)
                {
                    snapshot = _events.ToArray();
                }
            }

            if (snapshot is not null)
            {
                _ = _completion.TrySetResult(snapshot);
            }

            ReviewEventReceived?.Invoke(this, reviewEvent);
        }

        public void NotifyRateLimitReset(string label)
            => NotificationRequested?.Invoke(this, new NotificationMessage("fixture", label));

        public Task<IReadOnlyList<ReviewEvent>> WaitForEventsAsync(CancellationToken cancellationToken)
            => _completion.Task.WaitAsync(cancellationToken);
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
        Justification = "JSON serializer が fixture observation を生成する。")]
    private sealed class SubscriberObservation
    {
        public string Mode { get; set; } = string.Empty;

        public string[] Arguments { get; set; } = [];

        public string? GatewayUrl { get; set; }

        public string? ResourceUri { get; set; }

        public bool HasAuthorization { get; set; }

        public int ExitCode { get; set; }
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "JSON serializer が dummy launcher observation を生成する。")]
    private sealed class LauncherObservation
    {
        public string[] Arguments { get; set; } = [];

        public string WorkingDirectory { get; set; } = string.Empty;
    }
}
