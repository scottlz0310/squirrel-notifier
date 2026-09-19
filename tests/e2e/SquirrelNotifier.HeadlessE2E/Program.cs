using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.HeadlessE2E;

[SuppressMessage(
    "Design",
    "CA1031:Do not catch general exception types",
    Justification = "runner は scenario の失敗分類と artifact 生成を必ず行う必要がある。")]
[SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "context は finally で artifact 生成後に明示的に破棄する。")]
internal static class Program
{
    private const string _phase = "headless";
    private const string _repository = "fixture-owner/fixture-repository";
    private const int _pullRequestNumber = 307;
    private const string _parsedSessionId = "01234567-89ab-cdef-0123-456789abcdef";
    private const string _fixtureExitCode = "23";

    private static readonly Regex _guidRegex = new(
        @"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly string[] _artifactHints =
    [
        "sanitized.log",
        "command-lines.json",
        "subscriber-invocations.json",
        "settings-sanitized.json",
        "versions.json",
    ];

    private static async Task<int> Main(string[] args)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        RunnerOptions? options = null;
        ScenarioManifest? manifest = null;
        ScenarioContext? context = null;
        E2eFailureException? failure = null;

        try
        {
            options = RunnerOptions.Parse(args);
            manifest = LoadManifest(options.ScenarioFile);
            ValidateManifest(manifest);
            context = new ScenarioContext(manifest, options);
            await context.RunAsync().ConfigureAwait(false);
        }
        catch (E2eFailureException ex)
        {
            failure = ex;
        }
        catch (ContractE2EFailureException ex)
        {
            failure = new E2eFailureException(ex.Category, ex.Message);
        }
        catch (OperationCanceledException)
        {
            failure = new E2eFailureException("TIMEOUT", "scenario の制限時間を超過しました。");
        }
        catch (Exception ex)
        {
            failure = new E2eFailureException(
                "TEST_HARNESS_FAILED",
                $"headless E2E harness が予期しない例外で終了しました: {Sanitize(ex.Message)}");
        }
        finally
        {
            if (context is not null && manifest is not null)
            {
                try
                {
                    context.WriteArtifacts(startedAt, failure);
                }
                catch (Exception ex)
                {
                    failure ??= new E2eFailureException(
                        "TEST_HARNESS_FAILED",
                        $"E2E artifact の生成に失敗しました: {Sanitize(ex.Message)}");
                }

                context.Dispose();
            }
        }

        if (manifest is null)
        {
            await Console.Error.WriteLineAsync(
                $"headless E2E の開始に失敗しました: {failure?.Message ?? "manifest がありません。"}").ConfigureAwait(false);
            return 1;
        }

        if (failure is null)
        {
            await Console.Out.WriteLineAsync($"headless E2E 成功: {manifest.Id}").ConfigureAwait(false);
            return 0;
        }

        await Console.Error.WriteLineAsync(
            $"headless E2E 失敗: {manifest.Id}; category={failure.Category}; message={failure.Message}").ConfigureAwait(false);
        return 1;
    }

    private static ScenarioManifest LoadManifest(string path)
    {
        string json = File.ReadAllText(path);
        ScenarioManifest? manifest = JsonSerializer.Deserialize<ScenarioManifest>(json, _jsonOptions);
        return manifest ?? throw new E2eFailureException("TEST_HARNESS_FAILED", "scenario manifest が空です。");
    }

    private static void ValidateManifest(ScenarioManifest manifest)
    {
        if (manifest.SchemaVersion != 1
            || !string.Equals(manifest.Phase, _phase, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(manifest.Id)
            || string.IsNullOrWhiteSpace(manifest.Fixture)
            || string.IsNullOrWhiteSpace(manifest.ExpectedOutcome)
            || string.IsNullOrWhiteSpace(manifest.ScenarioKind)
            || manifest.TimeoutSeconds <= 0)
        {
            throw new E2eFailureException("TEST_HARNESS_FAILED", "scenario manifest の schema または必須項目が不正です。");
        }
    }

    private static string Sanitize(string value)
        => _guidRegex.Replace(value, "<redacted-session-id>");

    private sealed record RunnerOptions(
        string ScenarioFile,
        string RunRoot,
        string ArtifactsDirectory,
        string DummyLauncherPath,
        string? SubscriberFixturePath)
    {
        public static RunnerOptions Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < args.Length; index++)
            {
                string option = args[index];
                if (!option.StartsWith("--", StringComparison.Ordinal)
                    || index == args.Length - 1
                    || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new E2eFailureException("TEST_HARNESS_FAILED", "runner の引数が不正です。");
                }

                values[option[2..]] = args[++index];
            }

            string? subscriberFixturePath = values.TryGetValue("subscriber-fixture", out string? subscriberFixture)
                && !string.IsNullOrWhiteSpace(subscriberFixture)
                ? Path.GetFullPath(subscriberFixture)
                : null;

            return new RunnerOptions(
                GetRequired(values, "scenario-file"),
                GetRequired(values, "run-root"),
                GetRequired(values, "artifacts-directory"),
                GetRequired(values, "dummy-launcher"),
                subscriberFixturePath) with
            {
                ScenarioFile = Path.GetFullPath(GetRequired(values, "scenario-file")),
                RunRoot = Path.GetFullPath(GetRequired(values, "run-root")),
                ArtifactsDirectory = Path.GetFullPath(GetRequired(values, "artifacts-directory")),
                DummyLauncherPath = Path.GetFullPath(GetRequired(values, "dummy-launcher")),
            };
        }

        private static string GetRequired(Dictionary<string, string> values, string name)
        {
            return values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new E2eFailureException("TEST_HARNESS_FAILED", $"runner の必須引数 --{name} がありません。");
        }
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "JSON serializer が manifest を生成する。")]
    private sealed class ScenarioManifest
    {
        public int SchemaVersion { get; set; }

        public string Id { get; set; } = string.Empty;

        public string Phase { get; set; } = string.Empty;

        public int TimeoutSeconds { get; set; }

        public Dictionary<string, string> Components { get; set; } = new(StringComparer.Ordinal);

        public string Fixture { get; set; } = string.Empty;

        public string ExpectedOutcome { get; set; } = string.Empty;

        public string ScenarioKind { get; set; } = string.Empty;
    }

    private sealed class ScenarioContext : IDisposable
    {
        private const string _observationPathVariable = "SQUIRREL_NOTIFIER_E2E_DUMMY_OBSERVATION_PATH";
        private const string _outputFormatVariable = "SQUIRREL_NOTIFIER_E2E_DUMMY_OUTPUT_FORMAT";
        private const string _sessionIdVariable = "SQUIRREL_NOTIFIER_E2E_DUMMY_SESSION_ID";
        private const string _resumeExitCodeVariable = "SQUIRREL_NOTIFIER_E2E_DUMMY_RESUME_EXIT_CODE";

        private readonly ScenarioManifest _manifest;
        private readonly string _dummyLauncherPath;
        private readonly string? _subscriberFixturePath;
        private readonly Dictionary<string, string?> _environmentBeforeRun = new(StringComparer.Ordinal);
        private readonly List<string> _assertions = [];
        private static readonly string[] _resourceUris = ["queue://review/queue"];
        private readonly SettingsService _settingsService;
        private readonly LoggingService _loggingService;
        private readonly ReviewEvent _reviewEvent;
        private ReviewLauncherService? _launcherService;
        private bool _disposed;

        public ScenarioContext(ScenarioManifest manifest, RunnerOptions options)
        {
            _manifest = manifest;
            RunRoot = options.RunRoot;
            ArtifactsDirectory = options.ArtifactsDirectory;
            _dummyLauncherPath = options.DummyLauncherPath;
            _subscriberFixturePath = options.SubscriberFixturePath;

            if (!File.Exists(_dummyLauncherPath))
            {
                throw new E2eFailureException("TEST_HARNESS_FAILED", "dummy launcher の実行ファイルが見つかりません。");
            }

            Directory.CreateDirectory(RunRoot);
            Directory.CreateDirectory(ArtifactsDirectory);
            SettingsDirectory = Path.Combine(RunRoot, "settings");
            LogDirectory = Path.Combine(RunRoot, "logs");
            ObservationPath = Path.Combine(RunRoot, "dummy-invocations.jsonl");

            SetEnvironmentVariable(_observationPathVariable, ObservationPath);
            SetEnvironmentVariable(_outputFormatVariable, IsParsedOutput ? "codex" : "text");
            SetEnvironmentVariable(_sessionIdVariable, _parsedSessionId);
            SetEnvironmentVariable(_resumeExitCodeVariable, IsFailedResume ? _fixtureExitCode : "0");

            if (IsParsedOutput)
            {
                string dummyDirectory = Path.GetDirectoryName(_dummyLauncherPath)!;
                string currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                SetEnvironmentVariable("PATH", $"{dummyDirectory}{Path.PathSeparator}{currentPath}");
            }

            _settingsService = new SettingsService(SettingsDirectory, string.Empty);
            _loggingService = new LoggingService(LogDirectory);
            _reviewEvent = new ReviewEvent
            {
                EventId = $"e2e-{manifest.Id}",
                Repository = _repository,
                PrNumber = _pullRequestNumber,
                PrUrl = "https://github.com/fixture-owner/fixture-repository/pull/307",
                Reason = "initial-review",
                Source = "headless-e2e-fixture",
                Message = "決定的な headless E2E fixture",
            };
        }

        public string RunRoot { get; }

        public string ArtifactsDirectory { get; }

        public string SettingsDirectory { get; }

        public string LogDirectory { get; }

        public string ObservationPath { get; }

        private string SessionStorePath => Path.Combine(SettingsDirectory, "sessions.json");

        private bool IsParsedOutput
            => string.Equals(_manifest.ScenarioKind, "parsed-output", StringComparison.OrdinalIgnoreCase);

        private bool IsFailedResume
            => string.Equals(_manifest.ScenarioKind, "failed-resume", StringComparison.OrdinalIgnoreCase);

        public async Task RunAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_manifest.TimeoutSeconds));
            switch (_manifest.ScenarioKind)
            {
                case "client-assigned":
                    await RunClientAssignedAsync(timeout.Token).ConfigureAwait(false);
                    break;
                case "expired":
                    await RunExpiredAsync(timeout.Token).ConfigureAwait(false);
                    break;
                case "working-directory-mismatch":
                    await RunWorkingDirectoryMismatchAsync(timeout.Token).ConfigureAwait(false);
                    break;
                case "failed-resume":
                    await RunFailedResumeAsync(timeout.Token).ConfigureAwait(false);
                    break;
                case "disabled":
                    await RunDisabledAsync(timeout.Token).ConfigureAwait(false);
                    break;
                case "parsed-output":
                    await RunParsedOutputAsync(timeout.Token).ConfigureAwait(false);
                    break;
                case "subscriber-gateway-contract":
                case "gateway-auth-flow":
                    if (string.IsNullOrWhiteSpace(_subscriberFixturePath))
                    {
                        throw new E2eFailureException("TEST_HARNESS_FAILED", "subscriber fixture の実行ファイルが指定されていません。");
                    }

                    _assertions.AddRange(
                        await ContractE2ERunner.RunAsync(
                            _manifest.ScenarioKind,
                            RunRoot,
                            SettingsDirectory,
                            LogDirectory,
                            ArtifactsDirectory,
                            _subscriberFixturePath,
                            timeout.Token).ConfigureAwait(false));
                    break;
                default:
                    throw new E2eFailureException("TEST_HARNESS_FAILED", "未登録の scenario kind です。");
            }

            if (timeout.IsCancellationRequested)
            {
                throw new E2eFailureException("TIMEOUT", "scenario の制限時間を超過しました。");
            }
        }

        public void WriteArtifacts(DateTimeOffset startedAt, E2eFailureException? failure)
        {
            Directory.CreateDirectory(ArtifactsDirectory);
            DateTimeOffset completedAt = DateTimeOffset.UtcNow;
            IReadOnlyList<InvocationObservation> observations = TryReadObservations();

            WriteJson(
                "versions.json",
                new
                {
                    schemaVersion = 1,
                    phase = _phase,
                    scenarioId = _manifest.Id,
                    commitSha = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "local",
                    components = _manifest.Components,
                    os = Environment.OSVersion.VersionString,
                    runtime = RuntimeInformation.FrameworkDescription,
                    dotnetVersion = Environment.Version.ToString(),
                    driverAssembly = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
                    productAssembly = typeof(SettingsService).Assembly.GetName().Version?.ToString() ?? "unknown",
                });

            string logPath = Path.Combine(LogDirectory, "winui3.log");
            string log = File.Exists(logPath) ? File.ReadAllText(logPath) : string.Empty;
            File.WriteAllText(Path.Combine(ArtifactsDirectory, "sanitized.log"), Sanitize(log));

            string settingsPath = Path.Combine(SettingsDirectory, "settings.json");
            string settings = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : string.Empty;
            File.WriteAllText(Path.Combine(ArtifactsDirectory, "settings-sanitized.json"), Sanitize(settings));

            var commandLines = observations.Select((observation, index) => new
            {
                invocation = index + 1,
                command = IsParsedOutput ? "codex" : _dummyLauncherPath,
                arguments = observation.Arguments,
                workingDirectory = observation.WorkingDirectory,
                isResume = observation.IsResume,
            });
            File.WriteAllText(
                Path.Combine(ArtifactsDirectory, "command-lines.json"),
                Sanitize(JsonSerializer.Serialize(commandLines, _jsonOptions)));

            WriteJson(
                "result.json",
                new
                {
                    schemaVersion = 1,
                    phase = _phase,
                    scenarioId = _manifest.Id,
                    fixture = _manifest.Fixture,
                    expectedOutcome = _manifest.ExpectedOutcome,
                    outcome = failure is null ? "passed" : "failed",
                    startedAt,
                    completedAt,
                    durationMilliseconds = Math.Max(0, (completedAt - startedAt).TotalMilliseconds),
                    assertions = _assertions,
                    invocationCount = observations.Count,
                });

            if (failure is not null)
            {
                WriteJson(
                    "failure.json",
                    new
                    {
                        schemaVersion = 1,
                        phase = _phase,
                        scenarioId = _manifest.Id,
                        category = failure.Category,
                        component = "squirrel-notifier",
                        message = Sanitize(failure.Message),
                        startedAt,
                        completedAt,
                        artifactHints = _artifactHints,
                    });
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            foreach ((string name, string? value) in _environmentBeforeRun)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            _disposed = true;
        }

        private async Task RunClientAssignedAsync(CancellationToken cancellationToken)
        {
            ConfigureCustomSettings(sessionResumeEnabled: true);
            LauncherResult first = await LaunchAsync(LauncherRole.Reviewer, cancellationToken).ConfigureAwait(false);
            LauncherResult second = await LaunchAsync(LauncherRole.Reviewer, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(first, "first launch");
            EnsureSuccess(second, "resume launch");

            IReadOnlyList<InvocationObservation> observations = ReadObservations();
            Check(observations.Count == 2, "first launch と resume launch の 2 回だけ dummy を起動する");
            Check(!observations[0].IsResume, "first launch は新規起動として記録される");
            Check(observations[1].IsResume, "second launch は resume 起動として記録される");

            Guid firstSessionId = GetOptionGuid(observations[0], "--session-id");
            Guid resumedSessionId = GetOptionGuid(observations[1], "--resume");
            Check(firstSessionId == resumedSessionId, "resume 起動へ first launch と同じ UUID を渡す");
            Check(ReadStoreSessionIds().SequenceEqual([firstSessionId]), "sessions.json に同じ UUID の entry を 1 件保存する");
        }

        private async Task RunExpiredAsync(CancellationToken cancellationToken)
        {
            ConfigureCustomSettings(sessionResumeEnabled: true);
            LauncherResult first = await LaunchAsync(LauncherRole.Reviewer, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(first, "first launch");
            Guid firstSessionId = GetOptionGuid(ReadObservations()[0], "--session-id");

            RewriteLastUsedAtBeyondTtl();
            LauncherResult second = await LaunchAsync(LauncherRole.Reviewer, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(second, "expired entry の新規 launch");

            IReadOnlyList<InvocationObservation> observations = ReadObservations();
            Guid secondSessionId = GetOptionGuid(observations[1], "--session-id");
            Check(!observations[1].IsResume, "TTL 切れの entry は resume 起動へ使わない");
            Check(firstSessionId != secondSessionId, "TTL 切れの entry では新しい UUID を採番する");
            Check(ReadLog().Contains("TTL が切れています", StringComparison.Ordinal), "TTL 切れの理由をライブログへ記録する");
        }

        private async Task RunWorkingDirectoryMismatchAsync(CancellationToken cancellationToken)
        {
            ConfigureCustomSettings(sessionResumeEnabled: true);
            string firstCheckout = CreateCheckout("checkout-a");
            string secondCheckout = CreateCheckout("checkout-b");
            _settingsService.UpdateRepositoryCheckoutMappings(new Dictionary<string, string>
            {
                [_repository] = firstCheckout,
            });

            LauncherResult first = await LaunchAsync(LauncherRole.Reviewed, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(first, "first reviewed launch");
            Guid firstSessionId = GetOptionGuid(ReadObservations()[0], "--session-id");

            _settingsService.UpdateRepositoryCheckoutMappings(new Dictionary<string, string>
            {
                [_repository] = secondCheckout,
            });
            LauncherResult second = await LaunchAsync(LauncherRole.Reviewed, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(second, "working directory mismatch 後の新規 launch");

            IReadOnlyList<InvocationObservation> observations = ReadObservations();
            Guid secondSessionId = GetOptionGuid(observations[1], "--session-id");
            Check(observations[0].WorkingDirectory.Equals(firstCheckout, StringComparison.OrdinalIgnoreCase), "first launch は checkout-a で実行する");
            Check(observations[1].WorkingDirectory.Equals(secondCheckout, StringComparison.OrdinalIgnoreCase), "second launch は checkout-b で実行する");
            Check(!observations[1].IsResume, "working directory mismatch 後は resume 起動へ使わない");
            Check(firstSessionId != secondSessionId, "working directory mismatch 後は新しい UUID を採番する");
            Check(ReadLog().Contains("working directory が保存時と一致しません", StringComparison.Ordinal), "working directory mismatch の理由をライブログへ記録する");
        }

        private async Task RunFailedResumeAsync(CancellationToken cancellationToken)
        {
            ConfigureCustomSettings(sessionResumeEnabled: true);
            LauncherResult first = await LaunchAsync(LauncherRole.Reviewer, cancellationToken).ConfigureAwait(false);
            LauncherResult failedResume = await LaunchAsync(LauncherRole.Reviewer, cancellationToken).ConfigureAwait(false);
            LauncherResult next = await LaunchAsync(LauncherRole.Reviewer, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(first, "first launch");
            Check(!failedResume.Success, "dummy launcher の resume 非ゼロ終了を失敗として返す");
            Check(failedResume.ExitCode == int.Parse(_fixtureExitCode, CultureInfo.InvariantCulture), "resume 失敗の終了コードを保持する");
            Check(failedResume.ResumeFailureMessage == SessionResumeMessageFormatter.BuildFailure(), "resume 失敗時の保存情報破棄を結果へ記録する");
            EnsureSuccess(next, "resume 失敗後の新規 launch");

            IReadOnlyList<InvocationObservation> observations = ReadObservations();
            Check(observations.Count == 3, "resume 失敗時に自動 retry せず、明示した 3 回だけ起動する");
            Guid firstSessionId = GetOptionGuid(observations[0], "--session-id");
            Guid nextSessionId = GetOptionGuid(observations[2], "--session-id");
            Check(observations[1].IsResume, "2 回目の起動だけ resume 起動として記録される");
            Check(!observations[2].IsResume, "resume 失敗後の次回起動は新規起動になる");
            Check(firstSessionId != nextSessionId, "resume 失敗後は新しい UUID を採番する");
            Check(ReadStoreSessionIds().SequenceEqual([nextSessionId]), "resume 失敗時に旧 entry を破棄し、次回 entry だけを保存する");
            Check(ReadLog().Contains(SessionResumeMessageFormatter.BuildFailure(), StringComparison.Ordinal), "resume 失敗の理由をライブログへ記録する");
        }

        private async Task RunDisabledAsync(CancellationToken cancellationToken)
        {
            const string disabledArguments = "--fixture legacy --repository {owner}/{repo} --pull-request {prNumber} --reason {reason}";
            ConfigureCustomSettings(sessionResumeEnabled: false, arguments: disabledArguments, resumeArguments: string.Empty);
            LauncherResult result = await LaunchAsync(LauncherRole.Reviewer, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, "resume disabled launch");

            IReadOnlyList<InvocationObservation> observations = ReadObservations();
            string[] expectedArguments =
            [
                "--fixture",
                "legacy",
                "--repository",
                _repository,
                "--pull-request",
                _pullRequestNumber.ToString(CultureInfo.InvariantCulture),
                "--reason",
                "initial-review",
            ];
            Check(observations.Count == 1, "resume disabled では dummy を 1 回だけ起動する");
            Check(observations[0].Arguments.SequenceEqual(expectedArguments), "resume 導入前の command line と引数が完全一致する");
            Check(!File.Exists(SessionStorePath), "resume disabled では sessions.json を作成しない");
        }

        private async Task RunParsedOutputAsync(CancellationToken cancellationToken)
        {
            LauncherAgentDefinition definition = LauncherAgentCatalog.Find("codex")
                ?? throw new E2eFailureException("TEST_HARNESS_FAILED", "codex preset が見つかりません。");
            ConfigureCustomSettings(
                sessionResumeEnabled: true,
                commandPath: "codex",
                arguments: definition.ReviewerArgumentsTemplate,
                resumeArguments: definition.ReviewerResumeArgumentsTemplate,
                presetId: definition.Id);

            LauncherResult first = await LaunchAsync(LauncherRole.Reviewer, cancellationToken).ConfigureAwait(false);
            LauncherResult second = await LaunchAsync(LauncherRole.Reviewer, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(first, "ParsedFromOutput first launch");
            EnsureSuccess(second, "ParsedFromOutput resume launch");

            IReadOnlyList<InvocationObservation> observations = ReadObservations();
            Check(observations.Count == 2, "ParsedFromOutput では dummy を 2 回起動する");
            Check(!observations[0].IsResume, "ParsedFromOutput first launch は新規起動になる");
            Check(observations[1].IsResume, "ParsedFromOutput second launch は resume 起動になる");
            Check(observations[0].Arguments.Contains("--json", StringComparer.Ordinal), "Codex fixture へ構造化出力オプションを渡す");
            Check(observations[1].Arguments.Contains(_parsedSessionId, StringComparer.Ordinal), "ParsedFromOutput で抽出した UUID を resume 起動へ渡す");
            Check(ReadStoreSessionIds().SequenceEqual([Guid.ParseExact(_parsedSessionId, "D")]), "構造化出力から抽出した UUID を sessions.json へ保存する");
        }

        private void ConfigureCustomSettings(
            bool sessionResumeEnabled,
            string? commandPath = null,
            string? arguments = null,
            string? resumeArguments = null,
            string presetId = LauncherAgentCatalog.CustomPresetId)
        {
            string resolvedCommandPath = commandPath ?? _dummyLauncherPath;
            string normalArguments = arguments ?? "--fixture client-assigned --session-id {sessionId}";
            string normalResumeArguments = resumeArguments ?? "--fixture client-assigned --resume {sessionId}";
            _settingsService.UpdateSettings(
                "fixture-subscriber",
                "--fixture",
                "http://127.0.0.1:9",
                _resourceUris,
                30000,
                resolvedCommandPath,
                normalArguments,
                normalResumeArguments,
                resolvedCommandPath,
                normalArguments,
                normalResumeArguments,
                30000,
                sessionResumeEnabled,
                presetId,
                presetId);
            _launcherService = new ReviewLauncherService(_settingsService, _loggingService);
        }

        private async Task<LauncherResult> LaunchAsync(LauncherRole role, CancellationToken cancellationToken)
        {
            if (_launcherService is null)
            {
                throw new E2eFailureException("TEST_HARNESS_FAILED", "launcher service が初期化されていません。");
            }

            return await _launcherService.LaunchAsync(_reviewEvent, role, cancellationToken).ConfigureAwait(false);
        }

        private static void EnsureSuccess(LauncherResult result, string step)
        {
            if (result.Success)
            {
                return;
            }

            string category = result.ErrorMessage.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                ? "TIMEOUT"
                : "PRODUCT_CONTRACT_MISMATCH";
            throw new E2eFailureException(category, $"{step} が成功しませんでした。");
        }

        private void Check(bool condition, string description)
        {
            if (!condition)
            {
                throw new E2eFailureException("PRODUCT_CONTRACT_MISMATCH", description);
            }

            _assertions.Add(description);
        }

        private List<InvocationObservation> ReadObservations()
        {
            if (!File.Exists(ObservationPath))
            {
                throw new E2eFailureException("TEST_HARNESS_FAILED", "dummy launcher の invocation record がありません。");
            }

            var observations = new List<InvocationObservation>();
            foreach (string line in File.ReadLines(ObservationPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                InvocationObservation? observation = JsonSerializer.Deserialize<InvocationObservation>(line, _jsonOptions);
                if (observation is null)
                {
                    throw new E2eFailureException("TEST_HARNESS_FAILED", "dummy launcher の invocation record を解釈できません。");
                }

                observations.Add(observation);
            }

            return observations;
        }

        private List<InvocationObservation> TryReadObservations()
        {
            try
            {
                return ReadObservations();
            }
            catch (E2eFailureException)
            {
                return [];
            }
        }

        private static Guid GetOptionGuid(InvocationObservation observation, string option)
        {
            string? value = GetOptionValue(observation.Arguments, option);
            return value is not null && Guid.TryParseExact(value, "D", out Guid sessionId)
                ? sessionId
                : throw new E2eFailureException("PRODUCT_CONTRACT_MISMATCH", $"dummy launcher へ {option} の D 形式 UUID が渡されていません。");
        }

        private static string? GetOptionValue(string[] arguments, string option)
        {
            for (int index = 0; index < arguments.Length - 1; index++)
            {
                if (string.Equals(arguments[index], option, StringComparison.Ordinal))
                {
                    return arguments[index + 1];
                }
            }

            return null;
        }

        private List<Guid> ReadStoreSessionIds()
        {
            if (!File.Exists(SessionStorePath))
            {
                return [];
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(SessionStorePath));
            if (!document.RootElement.TryGetProperty("entries", out JsonElement entries)
                || entries.ValueKind != JsonValueKind.Object)
            {
                throw new E2eFailureException("TEST_HARNESS_FAILED", "sessions.json の entries が不正です。");
            }

            var sessionIds = new List<Guid>();
            foreach (JsonProperty entry in entries.EnumerateObject())
            {
                if (!entry.Value.TryGetProperty("sessionId", out JsonElement sessionIdElement)
                    || sessionIdElement.ValueKind != JsonValueKind.String
                    || !Guid.TryParse(sessionIdElement.GetString(), out Guid sessionId))
                {
                    throw new E2eFailureException("TEST_HARNESS_FAILED", "sessions.json の sessionId が不正です。");
                }

                sessionIds.Add(sessionId);
            }

            return sessionIds;
        }

        private void RewriteLastUsedAtBeyondTtl()
        {
            if (!File.Exists(SessionStorePath))
            {
                throw new E2eFailureException("TEST_HARNESS_FAILED", "TTL 検証用の sessions.json がありません。");
            }

            JsonObject document = JsonNode.Parse(File.ReadAllText(SessionStorePath))?.AsObject()
                ?? throw new E2eFailureException("TEST_HARNESS_FAILED", "sessions.json を JSON object として解釈できません。");
            JsonObject entries = document["entries"]?.AsObject()
                ?? throw new E2eFailureException("TEST_HARNESS_FAILED", "TTL 検証用の entries がありません。");
            string expiredAt = DateTimeOffset.UtcNow
                .Subtract(SessionResumeStore.DefaultTtl)
                .Subtract(TimeSpan.FromMinutes(1))
                .ToString("O", CultureInfo.InvariantCulture);

            foreach (KeyValuePair<string, JsonNode?> property in entries)
            {
                if (property.Value is JsonObject entry)
                {
                    entry["lastUsedAt"] = expiredAt;
                }
            }

            File.WriteAllText(SessionStorePath, document.ToJsonString(_jsonOptions));
        }

        private string CreateCheckout(string name)
        {
            string path = Path.Combine(RunRoot, "checkouts", name);
            Directory.CreateDirectory(Path.Combine(path, ".git"));
            return Path.GetFullPath(path);
        }

        private string ReadLog()
        {
            string path = Path.Combine(LogDirectory, "winui3.log");
            return File.Exists(path)
                ? File.ReadAllText(path)
                : throw new E2eFailureException("TEST_HARNESS_FAILED", "product log がありません。");
        }

        private void SetEnvironmentVariable(string name, string value)
        {
            if (!_environmentBeforeRun.ContainsKey(name))
            {
                _environmentBeforeRun[name] = Environment.GetEnvironmentVariable(name);
            }

            Environment.SetEnvironmentVariable(name, value);
        }

        private void WriteJson(string fileName, object value)
        {
            File.WriteAllText(
                Path.Combine(ArtifactsDirectory, fileName),
                Sanitize(JsonSerializer.Serialize(value, _jsonOptions)));
        }

        [SuppressMessage(
            "Performance",
            "CA1812:Avoid uninstantiated internal classes",
            Justification = "JSON serializer が invocation record を生成する。")]
        private sealed class InvocationObservation
        {
            public int ProcessId { get; set; }

            public string[] Arguments { get; set; } = [];

            public string WorkingDirectory { get; set; } = string.Empty;

            public bool StandardInputRedirected { get; set; }

            public string OutputFormat { get; set; } = string.Empty;

            public bool IsResume { get; set; }

            public int ExitCode { get; set; }
        }
    }

    [SuppressMessage(
        "Design",
        "CA1032:Implement standard exception constructors",
        Justification = "failure category を必須にする runner 内部例外である。")]
    private sealed class E2eFailureException : Exception
    {
        public E2eFailureException(string category, string message)
            : base(message)
        {
            Category = category;
        }

        public string Category { get; }
    }
}
