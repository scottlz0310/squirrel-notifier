// <copyright file="ResourceBridgeObservationSender.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Text.Json;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// 観測を、resource-bridge-cli の <c>call --tool publish_review_observation</c> で review-raven の内部面へ送る.
/// 既存の <see cref="EnqueueReviewService"/> と同じく、通信と認証は CLI に任せる.
/// </summary>
/// <remarks>
/// 内部面は、利用者ではなく<b>サービス主体</b>のトークンで呼ぶ。CLI の認証キャッシュは origin 単位で、同じ gateway の
/// 利用者のトークンと衝突するため、サービス主体のトークンは環境変数 <see cref="TokenEnvironmentVariable"/> から読み、
/// 子プロセスの <c>MCP_PROBE_AUTH_TOKEN</c> にだけ渡す（引数には載せず、設定ファイルにも保存せず、ログにも出さない）.
/// </remarks>
internal sealed class ResourceBridgeObservationSender : IObservationSender
{
    /// <summary>サービス主体のトークンを渡す環境変数.</summary>
    internal const string TokenEnvironmentVariable = "SQUIRREL_OBSERVATION_TOKEN";

    private const string _toolName = "publish_review_observation";
    private const string _cliTokenVariable = "MCP_PROBE_AUTH_TOKEN";
    private const int _callTimeoutMs = 30000;

    private readonly SettingsService _settingsService;
    private readonly IProcessRunner _processRunner;
    private readonly Func<string?> _getToken;

    public ResourceBridgeObservationSender(
        SettingsService settingsService,
        IProcessRunner? processRunner = null,
        Func<string?>? getToken = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _processRunner = processRunner ?? new ProcessRunner();
        _getToken = getToken ?? (static () => Environment.GetEnvironmentVariable(TokenEnvironmentVariable));
    }

    /// <inheritdoc/>
    public async Task<ObservationSendResult> SendAsync(ReviewObservation observation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);

        AppSettings settings = _settingsService.Settings;
        if (string.IsNullOrWhiteSpace(settings.ObservationPublishUrl))
        {
            return ObservationSendResult.Transient("OBSERVATION_URL_NOT_SET", "ObservationPublishUrl が未設定です。");
        }

        string? token = _getToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return ObservationSendResult.Transient("OBSERVATION_TOKEN_NOT_SET", $"環境変数 {TokenEnvironmentVariable} が未設定です。");
        }

        ProcessStartInfo psi = new()
        {
            FileName = SettingsService.ResolveCommandPath(settings.SubscriberCommandPath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        // "call" は先頭の positional 引数として認識される必要があるため、他の引数より先に追加する
        psi.ArgumentList.Add("call");
        if (!string.IsNullOrWhiteSpace(settings.SubscriberArguments))
        {
            foreach (string arg in McpSubscriptionService.ParseArguments(settings.SubscriberArguments))
            {
                psi.ArgumentList.Add(arg);
            }
        }

        psi.ArgumentList.Add("--url");
        psi.ArgumentList.Add(settings.ObservationPublishUrl);
        psi.ArgumentList.Add("--tool");
        psi.ArgumentList.Add(_toolName);
        psi.ArgumentList.Add("--args");
        psi.ArgumentList.Add(JsonSerializer.Serialize(observation));
        psi.ArgumentList.Add("--json");
        psi.Environment[_cliTokenVariable] = token;

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_callTimeoutMs);

        IProcessInstance? process = null;
        try
        {
            process = _processRunner.Start(psi);
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);

            return BuildResult(process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillProcess(process);
            return ObservationSendResult.Transient("OBSERVATION_TIMEOUT", "観測の送信がタイムアウトしました。");
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            KillProcess(process);
            return ObservationSendResult.Transient("OBSERVATION_CALL_FAILED", $"resource-bridge-cli を実行できません: {ex.Message}");
        }
        finally
        {
            process?.Dispose();
        }
    }

    // call モードの exit code: 0 = 成功, 1 = tool エラー, 2 = 認証エラー, 3 = 通信・使用方法エラー
    private static ObservationSendResult BuildResult(int exitCode, string stdout, string stderr)
    {
        CallToolResult? result = TryParse(stdout);
        string detail = result?.ExtractText() is string text && !string.IsNullOrWhiteSpace(text)
            ? text
            : (!string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim());

        return exitCode switch
        {
            0 => ObservationSendResult.Accepted,

            // tool が返した契約のエラー(error.code / error.retryable)。送り直しても成功しないものは、その cycle の送信をやめる
            1 => ReadContractError(detail),

            // 明示したトークンの失効・誤り。トークンを直せば、次の機会に送れる
            2 => ObservationSendResult.Transient(
                "OBSERVATION_AUTH_FAILED",
                $"gateway への認証に失敗しました。{TokenEnvironmentVariable} のサービス主体のトークンを確認してください。"),

            _ => IsNonRetryableCommunicationError(result?.ErrorCode)
                ? ObservationSendResult.Permanent(result!.ErrorCode!, "接続先が観測の送信を受け付けません（ツール名・引数・プロトコルの不一致）。")
                : ObservationSendResult.Transient("OBSERVATION_COMMUNICATION_FAILED", "通信エラーが発生しました。"),
        };
    }

    private static bool IsNonRetryableCommunicationError(string? errorCode)
        => string.Equals(errorCode, "TOOL_REQUEST_REJECTED", StringComparison.OrdinalIgnoreCase)
            || string.Equals(errorCode, "PROTOCOL_UNSUPPORTED", StringComparison.OrdinalIgnoreCase);

    private static ObservationSendResult ReadContractError(string detail)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(detail);
            if (document.RootElement.TryGetProperty("error", out JsonElement error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out JsonElement code)
                && code.ValueKind == JsonValueKind.String)
            {
                bool retryable = error.TryGetProperty("retryable", out JsonElement flag) && flag.ValueKind == JsonValueKind.True;
                string errorCode = code.GetString() ?? "UNKNOWN";

                // 本文は載せない。契約のエラーの code だけを記録する
                return retryable
                    ? ObservationSendResult.Transient(errorCode, "review-raven が一時的に受理できませんでした。")
                    : ObservationSendResult.Permanent(errorCode, "review-raven が観測を拒否しました。");
            }
        }
        catch (JsonException)
        {
            // 契約のエラーの形ではない。下で不明な拒否として扱う
        }

        return ObservationSendResult.Permanent("OBSERVATION_REJECTED", "review-raven が観測を拒否しました（理由の形式が不明です）。");
    }

    private static CallToolResult? TryParse(string stdout)
    {
        try
        {
            return string.IsNullOrWhiteSpace(stdout) ? null : JsonSerializer.Deserialize<CallToolResult>(stdout);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void KillProcess(IProcessInstance? process)
    {
        if (process == null)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // 既に終了している
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // OS が終了させられなかった。ここでできることはない
        }
    }
}
