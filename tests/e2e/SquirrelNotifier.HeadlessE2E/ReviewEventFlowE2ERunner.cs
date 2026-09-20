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
[SuppressMessage(
    "Reliability",
    "CA2007:Consider calling ConfigureAwait on the awaited task",
    Justification = "await using の初期化で作成した E2E fixture はメソッド終了時に確実に破棄する。")]
internal static class ReviewEventFlowE2ERunner
{
    private const string _subscriberObservationVariable = "SQUIRREL_NOTIFIER_E2E_SUBSCRIBER_OBSERVATION_PATH";
    private const string _tokenCachePathVariable = "SQUIRREL_NOTIFIER_E2E_SUBSCRIBER_TOKEN_CACHE_PATH";
    private const string _flowVariable = "SQUIRREL_NOTIFIER_E2E_SUBSCRIBER_FLOW";
    private const string _preflightFailureVariable = "SQUIRREL_NOTIFIER_E2E_SUBSCRIBER_PREFLIGHT_FAILURE";
    private const string _delayVariable = "SQUIRREL_NOTIFIER_E2E_SUBSCRIBER_DELAY_MS";
    private const string _observationMutexName = "SquirrelNotifier.E2E.DummySubscriber.Observation";
    private const string _launcherObservationMutexName = "SquirrelNotifier.E2E.DummyLauncher.Observation";
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

                await RunFailedRegistrationCasesAsync(
                    settingsDirectory,
                    logDirectory,
                    dummyLauncherPath,
                    server.Endpoint.ToString(),
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
            WriteLauncherArtifact(runRoot, artifactsDirectory);
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
            dummyLauncherPath,
            autoReviewStartEnabled: true);
        var logging = new LoggingService(caseLogDirectory);
        var notifications = new NotificationService();
        await using var processing = new ReviewEventProcessingHarness(
            settings,
            logging,
            notifications,
            _expectedEventCount);
        var subscription = new McpSubscriptionService(
            settings,
            notifications,
            logging,
            maxRetries: 0,
            startTimeoutMs: 5000,
            dependencyWaitBudgetMs: 0);
        int subscriberObservationStart = ReadSubscriberObservations().Count;
        int launcherObservationStart = ReadLauncherObservations(runRoot).Count;

        try
        {
            environment.Set(_delayVariable, "100");
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
                Check(
                    subscription.State == SubscriptionState.Stopped,
                    assertions,
                    "初期状態が Stopped の購読開始を RegisterAsync の中で実行する");
            }
            else
            {
                Check(
                    subscription.State == SubscriptionState.Stopped,
                    assertions,
                    "Stopped 状態の購読からレビュー登録を開始する");
            }

            int confirmationCount = 0;
            var registration = new ReviewRegistrationService(
                subscription,
                new EnqueueReviewService(settings, logging));

            if (induceInitialError)
            {
                environment.Set(_preflightFailureVariable, "1");
                ReviewRegistrationResult failedRegistration = await registration.RegisterAsync(
                    new PrReference("fixture-owner", "fixture-repository", _pullRequestNumber),
                    "opened",
                    _ =>
                    {
                        confirmationCount++;
                        return Task.FromResult(true);
                    },
                    cancellationToken).ConfigureAwait(false);

                Check(
                    failedRegistration.Outcome == ReviewRegistrationOutcome.SubscriptionStartFailed,
                    assertions,
                    "Error 状態では RegisterAsync が購読開始失敗として終了する");
                Check(
                    subscription.State == SubscriptionState.Error,
                    assertions,
                    "RegisterAsync の購読開始失敗で購読を Error 状態へ遷移する");
                IReadOnlyList<SubscriberObservation> failedStartObservations = ReadSubscriberObservations()
                    .Skip(subscriberObservationStart)
                    .ToArray();
                Check(
                    failedStartObservations.All(
                        observation => observation.Mode is not ("version" or "call" or "subscription")),
                    assertions,
                    "購読開始に失敗した場合は enqueue を呼び出さない");
                environment.Set(_preflightFailureVariable, null);
            }

            ReviewRegistrationResult registrationResult = await registration.RegisterAsync(
                new PrReference("fixture-owner", "fixture-repository", _pullRequestNumber),
                "opened",
                _ =>
                {
                    confirmationCount++;
                    return Task.FromResult(true);
                },
                cancellationToken).ConfigureAwait(false);

            Check(
                registrationResult.Outcome == ReviewRegistrationOutcome.Registered,
                assertions,
                $"{caseId}: 購読開始後に enqueue_review が成功する");
            Check(
                confirmationCount == (startAlreadyRunning ? 0 : induceInitialError ? 2 : 1),
                assertions,
                $"{caseId}: Stopped / Error の購読開始前に確認を要求する");
            Check(
                subscription.State == SubscriptionState.Running,
                assertions,
                $"{caseId}: enqueue 前に購読が Running へ到達する");

            IReadOnlyList<ReviewEvent> reviewEvents = await processing
                .WaitForEventsAsync(cancellationToken)
                .ConfigureAwait(false);
            AssertReviewEvents(reviewEvents, caseId, assertions);

            IReadOnlyList<SubscriberObservation> observationsBeforeStop = ReadSubscriberObservations()
                .Skip(subscriberObservationStart)
                .ToArray();
            AssertEnqueueAfterSubscription(observationsBeforeStop, caseId, assertions);
            AssertPreflightBehavior(observationsBeforeStop, caseId, induceInitialError, assertions);

            await subscription.StopAsync().ConfigureAwait(false);

            _ = await WaitForLauncherObservationsAsync(
                runRoot,
                launcherObservationStart + _expectedEventCount,
                cancellationToken).ConfigureAwait(false);
            await processing.WaitForIdleAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<LauncherObservation> launcherObservations = ReadLauncherObservations(runRoot)
                .Skip(launcherObservationStart)
                .ToArray();
            Check(
                launcherObservations.Count == _expectedEventCount,
                assertions,
                $"{caseId}: 各通知イベントを一度ずつ dummy launcher へ渡す");

            AssertLauncherObservations(
                reviewEvents,
                launcherObservations,
                caseSettingsDirectory,
                caseId,
                assertions);
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
        string dummyLauncherPath,
        bool autoReviewStartEnabled = false)
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
        settings.UpdateAutoReviewStartEnabled(autoReviewStartEnabled);
        return settings;
    }

    private static async Task RunFailedRegistrationCasesAsync(
        string settingsDirectory,
        string logDirectory,
        string dummyLauncherPath,
        string successGatewayUrl,
        string subscriberFixturePath,
        EnvironmentScope environment,
        List<string> assertions,
        CancellationToken cancellationToken)
    {
        await RunFailedRegistrationCaseAsync(
            "authentication-required",
            successGatewayUrl,
            settingsDirectory,
            logDirectory,
            dummyLauncherPath,
            subscriberFixturePath,
            environment,
            assertions,
            expectAuthenticationRequired: false,
            preflightFailureMode: "auth",
            expectedErrorText: "AUTH_LOGIN_REQUIRED",
            startTimeoutMs: 5000,
            delayMs: 100,
            cancellationToken).ConfigureAwait(false);

        await RunFailedRegistrationCaseAsync(
            "timeout",
            successGatewayUrl,
            settingsDirectory,
            logDirectory,
            dummyLauncherPath,
            subscriberFixturePath,
            environment,
            assertions,
            expectAuthenticationRequired: false,
            preflightFailureMode: null,
            expectedErrorText: "時間内",
            startTimeoutMs: 250,
            delayMs: 6000,
            cancellationToken).ConfigureAwait(false);

        await RunCancelledRegistrationCaseAsync(
            settingsDirectory,
            logDirectory,
            dummyLauncherPath,
            successGatewayUrl,
            subscriberFixturePath,
            environment,
            assertions,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task RunFailedRegistrationCaseAsync(
        string caseId,
        string gatewayUrl,
        string settingsDirectory,
        string logDirectory,
        string dummyLauncherPath,
        string subscriberFixturePath,
        EnvironmentScope environment,
        List<string> assertions,
        bool expectAuthenticationRequired,
        string? preflightFailureMode,
        string expectedErrorText,
        int startTimeoutMs,
        int delayMs,
        CancellationToken cancellationToken)
    {
        environment.Set(_preflightFailureVariable, preflightFailureMode);
        environment.Set(_delayVariable, delayMs.ToString(CultureInfo.InvariantCulture));

        string caseSettingsDirectory = Path.Combine(settingsDirectory, $"registration-{caseId}");
        string caseLogDirectory = Path.Combine(logDirectory, $"registration-{caseId}");
        SettingsService settings = CreateSettings(caseSettingsDirectory, subscriberFixturePath, gatewayUrl, dummyLauncherPath);
        var logging = new LoggingService(caseLogDirectory);
        var notifications = new NotificationService();
        var subscription = new McpSubscriptionService(
            settings,
            notifications,
            logging,
            maxRetries: 0,
            startTimeoutMs: startTimeoutMs,
            dependencyWaitBudgetMs: 0);
        int subscriberObservationStart = ReadSubscriberObservations().Count;

        try
        {
            var registration = new ReviewRegistrationService(
                subscription,
                new EnqueueReviewService(settings, logging));
            int confirmationCount = 0;
            ReviewRegistrationResult result = await registration.RegisterAsync(
                new PrReference("fixture-owner", "fixture-repository", _pullRequestNumber),
                "opened",
                _ =>
                {
                    confirmationCount++;
                    return Task.FromResult(true);
                },
                cancellationToken).ConfigureAwait(false);

            Check(
                result.Outcome == ReviewRegistrationOutcome.SubscriptionStartFailed,
                assertions,
                $"{caseId}: RegisterAsync が購読開始失敗として終了する");
            Check(
                confirmationCount == 1,
                assertions,
                $"{caseId}: 停止中の購読開始確認を RegisterAsync から一度だけ要求する");
            Check(
                result.IsAuthenticationRequired == expectAuthenticationRequired,
                assertions,
                $"{caseId}: 認証要求状態を RegisterAsync の結果へ反映する");
            Check(
                result.ErrorMessage.Contains(expectedErrorText, StringComparison.Ordinal),
                assertions,
                $"{caseId}: 購読開始失敗の原因を RegisterAsync の結果へ反映する");

            IReadOnlyList<SubscriberObservation> observations = ReadSubscriberObservations()
                .Skip(subscriberObservationStart)
                .ToArray();
            Check(
                observations.All(observation => observation.Mode is not "call"),
                assertions,
                $"{caseId}: 購読開始が失敗した場合は enqueue_review を呼び出さない");
        }
        finally
        {
            await subscription.StopAsync().ConfigureAwait(false);
            await subscription.DisposeAsync().ConfigureAwait(false);
            environment.Set(_delayVariable, "100");
            environment.Set(_preflightFailureVariable, null);
        }
    }

    private static async Task RunCancelledRegistrationCaseAsync(
        string settingsDirectory,
        string logDirectory,
        string dummyLauncherPath,
        string gatewayUrl,
        string subscriberFixturePath,
        EnvironmentScope environment,
        List<string> assertions,
        CancellationToken cancellationToken)
    {
        environment.Set(_preflightFailureVariable, null);
        environment.Set(_delayVariable, "100");

        string caseSettingsDirectory = Path.Combine(settingsDirectory, "registration-cancelled");
        string caseLogDirectory = Path.Combine(logDirectory, "registration-cancelled");
        SettingsService settings = CreateSettings(caseSettingsDirectory, subscriberFixturePath, gatewayUrl, dummyLauncherPath);
        var logging = new LoggingService(caseLogDirectory);
        var notifications = new NotificationService();
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
            var registration = new ReviewRegistrationService(
                subscription,
                new EnqueueReviewService(settings, logging));
            using var registrationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await registrationCancellation.CancelAsync().ConfigureAwait(false);
            int confirmationCount = 0;
            ReviewRegistrationResult result = await registration.RegisterAsync(
                new PrReference("fixture-owner", "fixture-repository", _pullRequestNumber),
                "opened",
                _ =>
                {
                    confirmationCount++;
                    return Task.FromResult(true);
                },
                registrationCancellation.Token).ConfigureAwait(false);

            Check(
                result.Outcome == ReviewRegistrationOutcome.Cancelled,
                assertions,
                "cancelled: RegisterAsync のキャンセルを成功登録に変換しない");
            Check(
                confirmationCount == 0,
                assertions,
                "cancelled: キャンセル済みの登録では購読開始確認を要求しない");
            IReadOnlyList<SubscriberObservation> observations = ReadSubscriberObservations()
                .Skip(subscriberObservationStart)
                .ToArray();
            Check(
                observations.All(observation => observation.Mode is not "call"),
                assertions,
                "cancelled: キャンセル時は enqueue_review を呼び出さない");
        }
        finally
        {
            await subscription.StopAsync().ConfigureAwait(false);
            await subscription.DisposeAsync().ConfigureAwait(false);
        }
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

    private static async Task<IReadOnlyList<LauncherObservation>> WaitForLauncherObservationsAsync(
        string runRoot,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            IReadOnlyList<LauncherObservation> observations = ReadLauncherObservations(runRoot);
            if (observations.Count >= expectedCount)
            {
                return observations;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        throw new ContractE2EFailureException(
            "PRODUCT_CONTRACT_MISMATCH",
            $"dummy launcher の観測が {expectedCount} 件に到達しませんでした。");
    }

    private static void AssertLauncherObservations(
        IReadOnlyList<ReviewEvent> reviewEvents,
        IReadOnlyList<LauncherObservation> launcherObservations,
        string caseSettingsDirectory,
        string caseId,
        List<string> assertions)
    {
        bool[] matched = new bool[launcherObservations.Count];
        string expectedWorkingDirectory = Path.GetFullPath(
            Path.Combine(caseSettingsDirectory, "launcher-workspace", "reviewer"));

        foreach (ReviewEvent reviewEvent in reviewEvents)
        {
            string[] expectedArguments = BuildLauncherArguments(reviewEvent);
            int matchingIndex = -1;
            for (int index = 0; index < launcherObservations.Count; index++)
            {
                LauncherObservation observation = launcherObservations[index];
                if (!matched[index]
                    && observation.Arguments.SequenceEqual(expectedArguments)
                    && string.Equals(
                        Path.GetFullPath(observation.WorkingDirectory),
                        expectedWorkingDirectory,
                        StringComparison.OrdinalIgnoreCase))
                {
                    matchingIndex = index;
                    break;
                }
            }

            Check(
                matchingIndex >= 0,
                assertions,
                $"{caseId}: owner/repo・PR 番号・reason と working directory を launcher 観測から照合する ({reviewEvent.PrNumber})");
            if (matchingIndex < 0)
            {
                continue;
            }

            matched[matchingIndex] = true;
        }

        Check(
            matched.All(static value => value),
            assertions,
            $"{caseId}: launcher 観測に期待外の invocation を含めない");
    }

    private static string[] BuildLauncherArguments(ReviewEvent reviewEvent)
        =>
        [
            "--repository",
            reviewEvent.Repository,
            "--pull-request",
            reviewEvent.PrNumber.ToString(CultureInfo.InvariantCulture),
            "--reason",
            reviewEvent.Reason,
        ];

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

    private sealed class ReviewEventProcessingHarness : IAsyncDisposable
    {
        private readonly ReviewEventProcessingCoordinator _processingCoordinator;
        private readonly ReviewStartCoordinator _reviewStartCoordinator;
        private readonly ReviewEventCleanupCoordinator _cleanupCoordinator;
        private readonly AutoPauseResumeScheduler _resumeScheduler;
        private readonly PendingReviewStartQueue _pendingQueue;
        private readonly SemaphoreSlim _processingGate = new(1, 1);
        private readonly TaskCompletionSource<IReadOnlyList<ReviewEvent>> _eventsCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _lock = new();
        private readonly List<ReviewEvent> _events = [];
        private readonly List<Task> _backgroundTasks = [];
        private readonly int _expectedEventCount;
        private Exception? _backgroundException;

        public ReviewEventProcessingHarness(
            SettingsService settings,
            LoggingService logging,
            NotificationService notifications,
            int expectedEventCount)
        {
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(logging);
            ArgumentNullException.ThrowIfNull(notifications);
            _expectedEventCount = expectedEventCount;

            var launcherService = new ReviewLauncherService(settings, logging);
            _pendingQueue = new PendingReviewStartQueue();
            _resumeScheduler = new AutoPauseResumeScheduler();
            var autoPauseGate = new AutoPauseGate();
            var rateLimitSnapshotService = new RateLimitSnapshotService(new RateLimitFileService(settings.SettingsDirectory));
            _cleanupCoordinator = new ReviewEventCleanupCoordinator(
                new AlwaysOpenPullRequestStatusClient(),
                logging);
            var cycleCoordinator = new ReviewCycleCoordinator(
                new ReviewCycleStore(settings.SettingsDirectory),
                logging);
            _reviewStartCoordinator = new ReviewStartCoordinator(
                launcherService,
                settings,
                rateLimitSnapshotService,
                autoPauseGate,
                _pendingQueue,
                _resumeScheduler,
                logging,
                cycleCoordinator);
            _processingCoordinator = new ReviewEventProcessingCoordinator(
                new ReviewEventCollectionCoordinator(),
                _cleanupCoordinator,
                _pendingQueue,
                logging,
                _reviewStartCoordinator.TryStartAutomaticallyAsync,
                cycleCoordinator);

            notifications.ReviewEventReceived += OnReviewEventReceived;
            launcherService.RunCompleted += OnLauncherRunCompleted;
            _reviewStartCoordinator.StartAbandoned += OnLauncherRunCompleted;
        }

        public async Task<IReadOnlyList<ReviewEvent>> WaitForEventsAsync(CancellationToken cancellationToken)
            => await _eventsCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        public async Task WaitForIdleAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                Exception? backgroundException;
                lock (_lock)
                {
                    backgroundException = _backgroundException;
                }

                if (backgroundException is not null)
                {
                    throw new ContractE2EFailureException(
                        "PRODUCT_CONTRACT_MISMATCH",
                        $"通知から reviewer 起動までの処理に失敗しました: {backgroundException.Message}");
                }

                if (!_reviewStartCoordinator.IsBusy && _pendingQueue.Count == 0)
                {
                    return;
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Task[] backgroundTasks;
            lock (_lock)
            {
                backgroundTasks = [.. _backgroundTasks];
            }

            try
            {
                await Task.WhenAll(backgroundTasks).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _backgroundException ??= ex;
                }
            }

            await _cleanupCoordinator.DisposeAsync().ConfigureAwait(false);
            _resumeScheduler.Dispose();
            _processingGate.Dispose();
        }

        private void OnReviewEventReceived(object? sender, ReviewEvent reviewEvent)
        {
            Task task = ProcessEventAsync(reviewEvent);
            lock (_lock)
            {
                _backgroundTasks.Add(task);
            }
        }

        private async Task ProcessEventAsync(ReviewEvent reviewEvent)
        {
            await _processingGate.WaitAsync().ConfigureAwait(false);
            try
            {
                _ = await _processingCoordinator.ProcessAsync(reviewEvent).ConfigureAwait(false);
                IReadOnlyList<ReviewEvent>? snapshot = null;
                lock (_lock)
                {
                    _events.Add(reviewEvent);
                    if (_events.Count >= _expectedEventCount)
                    {
                        snapshot = _events.ToArray();
                    }
                }

                if (snapshot is not null)
                {
                    _ = _eventsCompletion.TrySetResult(snapshot);
                }
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _backgroundException ??= ex;
                }

                _ = _eventsCompletion.TrySetException(ex);
            }
            finally
            {
                _processingGate.Release();
            }
        }

        private void OnLauncherRunCompleted(object? sender, EventArgs e)
        {
            Task task = ProcessPendingAsync();
            lock (_lock)
            {
                _backgroundTasks.Add(task);
            }
        }

        private async Task ProcessPendingAsync()
        {
            await _processingGate.WaitAsync().ConfigureAwait(false);
            try
            {
                _ = await _processingCoordinator.ProcessPendingAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _backgroundException ??= ex;
                }
            }
            finally
            {
                _processingGate.Release();
            }
        }
    }

    private sealed class AlwaysOpenPullRequestStatusClient : IPullRequestStatusClient
    {
        public Task<PullRequestLifecycleState> GetStateAsync(
            string repository,
            int prNumber,
            CancellationToken cancellationToken)
            => Task.FromResult(PullRequestLifecycleState.Open);
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

        using var mutex = new Mutex(false, _launcherObservationMutexName);
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
                throw new ContractE2EFailureException(
                    "TEST_HARNESS_FAILED",
                    "dummy launcher observation の mutex を取得できませんでした。");
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
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
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

    private static void WriteLauncherArtifact(string runRoot, string artifactsDirectory)
    {
        Directory.CreateDirectory(artifactsDirectory);
        string observationPath = Path.Combine(runRoot, "dummy-invocations.jsonl");
        IReadOnlyList<LauncherObservation> observations = File.Exists(observationPath)
            ? ReadLauncherObservations(runRoot)
            : [];
        File.WriteAllText(
            Path.Combine(artifactsDirectory, "launcher-invocations.json"),
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
