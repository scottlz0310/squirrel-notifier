using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace SquirrelNotifier.E2EDummyLauncher;

internal static class Program
{
    private const string _codexAgentMessage = "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"dummy agent response\"}}";
    private const string _codexThreadStartedPrefix = "{\"type\":\"thread.started\",\"thread_id\":\"";
    private const string _codexThreadStartedSuffix = "\"}";
    private const string _completedMessage = "dummy launcher completed";
    private const string _failureMessage = "dummy launcher が fixture の失敗終了コードを返しました。";
    private const string _observationPathVariable = "SQUIRREL_NOTIFIER_E2E_DUMMY_OBSERVATION_PATH";
    private const string _outputFormatVariable = "SQUIRREL_NOTIFIER_E2E_DUMMY_OUTPUT_FORMAT";
    private const string _sessionIdVariable = "SQUIRREL_NOTIFIER_E2E_DUMMY_SESSION_ID";
    private const string _resumeExitCodeVariable = "SQUIRREL_NOTIFIER_E2E_DUMMY_RESUME_EXIT_CODE";

    private static readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "fixture process は設定エラーを決定的な終了コードへ変換する必要がある。")]
    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"dummy launcher の fixture 設定が不正です: {ex.Message}");
            return 90;
        }
    }

    [SuppressMessage(
        "Globalization",
        "CA1303:Do not pass literals as localized parameters",
        Justification = "dummy launcher の stdout / stderr は決定的な wire fixture であり、翻訳対象ではない。")]
    private static int Run(string[] args)
    {
        string observationPath = GetRequiredEnvironmentVariable(_observationPathVariable);
        string outputFormat = Environment.GetEnvironmentVariable(_outputFormatVariable) ?? "text";
        bool isResume = args.Any(static argument =>
            string.Equals(argument, "resume", StringComparison.OrdinalIgnoreCase)
            || string.Equals(argument, "--resume", StringComparison.OrdinalIgnoreCase));
        int exitCode = isResume ? ReadExitCode(_resumeExitCodeVariable) : 0;

        var observation = new InvocationObservation
        {
            ProcessId = Environment.ProcessId,
            Arguments = args,
            WorkingDirectory = Environment.CurrentDirectory,
            StandardInputRedirected = Console.IsInputRedirected,
            OutputFormat = outputFormat,
            IsResume = isResume,
            ExitCode = exitCode,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(observationPath)!);
        File.AppendAllText(
            observationPath,
            JsonSerializer.Serialize(observation, _serializerOptions) + Environment.NewLine);

        if (string.Equals(outputFormat, "codex", StringComparison.OrdinalIgnoreCase))
        {
            string sessionId = GetRequiredEnvironmentVariable(_sessionIdVariable);
            if (!Guid.TryParseExact(sessionId, "D", out _))
            {
                throw new InvalidOperationException("Codex fixture の session ID が D 形式 UUID ではありません。");
            }

            Console.WriteLine(string.Concat(_codexThreadStartedPrefix, sessionId, _codexThreadStartedSuffix));
            Console.WriteLine(_codexAgentMessage);
        }
        else
        {
            Console.WriteLine(_completedMessage);
        }

        if (exitCode != 0)
        {
            Console.Error.WriteLine(_failureMessage);
        }

        return exitCode;
    }

    private static string GetRequiredEnvironmentVariable(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"環境変数 {name} が設定されていません。")
            : value;
    }

    private static int ReadExitCode(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out int exitCode) && exitCode is >= 0 and <= 255
            ? exitCode
            : throw new InvalidOperationException($"環境変数 {name} には 0〜255 の整数が必要です。");
    }

    private sealed class InvocationObservation
    {
        public int ProcessId { get; init; }

        public string[] Arguments { get; init; } = [];

        public string WorkingDirectory { get; init; } = string.Empty;

        public bool StandardInputRedirected { get; init; }

        public string OutputFormat { get; init; } = string.Empty;

        public bool IsResume { get; init; }

        public int ExitCode { get; init; }
    }
}
