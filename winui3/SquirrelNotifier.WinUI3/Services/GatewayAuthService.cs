// <copyright file="GatewayAuthService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// <c>mcp-resource-subscriber --login</c> を呼び出して mcp-gateway の OAuth device flow 認証を実行・監視するサービス.
/// </summary>
internal sealed class GatewayAuthService
{
    private const int _loginTimeoutMs = 180000; // 3分間

    private static readonly Regex _urlRegex = new(
        @"https?://[^\s""'<>()]+",
        RegexOptions.Compiled);

    private static readonly Regex _userCodeRegex = new(
        @"(?:user_code[""]?\s*[:=]\s*[""]?|code[""]?\s*[:=]\s*[""]?|Code:\s*|user code:\s*)([A-Za-z0-9\-]{4,16})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex _queryUserCodeRegex = new(
        @"[?&](?:user_code|code)=([A-Za-z0-9\-]{4,16})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly SettingsService _settingsService;
    private readonly LoggingService _loggingService;
    private readonly McpSubscriptionService _subscriptionService;
    private readonly IProcessRunner _processRunner;
    private readonly IBrowserLauncher _browserLauncher;

    private readonly object _outputLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="GatewayAuthService"/> class.
    /// </summary>
    /// <param name="settingsService">設定サービス.</param>
    /// <param name="loggingService">ログサービス.</param>
    /// <param name="subscriptionService">購読サービス.</param>
    /// <param name="processRunner">プロセス実行インスタンス（モック用、省略可）.</param>
    /// <param name="browserLauncher">ブラウザ起動インスタンス（モック用、省略可）.</param>
    public GatewayAuthService(
        SettingsService settingsService,
        LoggingService loggingService,
        McpSubscriptionService subscriptionService,
        IProcessRunner? processRunner = null,
        IBrowserLauncher? browserLauncher = null)
    {
        _settingsService = settingsService;
        _loggingService = loggingService;
        _subscriptionService = subscriptionService;
        _processRunner = processRunner ?? new ProcessRunner();
        _browserLauncher = browserLauncher ?? new SystemBrowserLauncher();
    }

    private sealed class AuthState
    {
        public string? DetectedUrl { get; set; }

        public string? DetectedUserCode { get; set; }

        public bool HasLaunchedBrowser { get; set; }
    }

    /// <summary>
    /// ログや UI メッセージから認証トークンやシークレット情報をマスク・サニタイズします.
    /// </summary>
    /// <param name="message">サニタイズ対象のメッセージ文字列.</param>
    /// <returns>シークレットがマスクされた文字列.</returns>
    public static string SanitizeLogMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }

        // Bearer トークン, token=..., access_token, refresh_token, client_secret などのパターンを包括的にマスク
        string sanitized = Regex.Replace(
            message,
            @"(bearer\s+|token[=:]\s*|""?(?:access_token|refresh_token|client_secret)""?\s*[:=]\s*""?)[A-Za-z0-9_\-\.\+\/%=]+",
            "$1***",
            RegexOptions.IgnoreCase);

        return sanitized;
    }

    /// <summary>
    /// mcp-gateway 認証を非同期に実行します.
    /// </summary>
    /// <param name="progress">進捗通知用のプログレスインターフェース（省略可）.</param>
    /// <param name="cancellationToken">キャンセルトークン.</param>
    /// <returns>最終的な認証進捗状況.</returns>
    public async Task<GatewayAuthProgress> LoginAsync(
        IProgress<GatewayAuthProgress>? progress,
        CancellationToken cancellationToken)
    {
        GatewayAuthProgress resultProgress = new()
        {
            Stage = GatewayAuthStage.Starting,
        };

        progress?.Report(resultProgress);

        AppSettings settings = _settingsService.Settings;
        string resolvedPath = SettingsService.ResolveCommandPath(settings.SubscriberCommandPath);
        string gatewayUrl = settings.GatewayUrl;

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_loginTimeoutMs);

        await _loggingService.WriteAsync($"[AUTH] Starting mcp-gateway login process: {resolvedPath} --login").ConfigureAwait(false);

        ProcessStartInfo psi = new()
        {
            FileName = resolvedPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        psi.ArgumentList.Add("--login");

        if (!string.IsNullOrWhiteSpace(settings.SubscriberArguments))
        {
            foreach (string arg in McpSubscriptionService.ParseArguments(settings.SubscriberArguments))
            {
                psi.ArgumentList.Add(arg);
            }
        }

        if (!string.IsNullOrWhiteSpace(gatewayUrl))
        {
            psi.ArgumentList.Add("--url");
            psi.ArgumentList.Add(gatewayUrl);
        }

        IProcessInstance? process = null;
        try
        {
            process = _processRunner.Start(psi);

            AuthState state = new();
            StringBuilder stderrOutput = new();

            Task readStdoutTask = Task.Run(async () =>
            {
                while (!process.StandardOutput.EndOfStream)
                {
                    string? line = await process.StandardOutput.ReadLineAsync(cts.Token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    await ProcessOutputLineAsync(
                        line,
                        resultProgress,
                        progress,
                        state).ConfigureAwait(false);
                }
            }, cts.Token);

            Task readStderrTask = Task.Run(async () =>
            {
                while (!process.StandardError.EndOfStream)
                {
                    string? line = await process.StandardError.ReadLineAsync(cts.Token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    lock (stderrOutput)
                    {
                        if (stderrOutput.Length < 2048)
                        {
                            stderrOutput.AppendLine(line);
                        }
                    }

                    await ProcessOutputLineAsync(
                        line,
                        resultProgress,
                        progress,
                        state).ConfigureAwait(false);
                }
            }, cts.Token);

            Task waitForExitTask = process.WaitForExitAsync(cts.Token);
            await Task.WhenAll(readStdoutTask, readStderrTask, waitForExitTask).ConfigureAwait(false);

            if (process.ExitCode == 0)
            {
                await _loggingService.WriteAsync("[AUTH] mcp-gateway login process completed successfully.").ConfigureAwait(false);
                resultProgress.Stage = GatewayAuthStage.Success;
                progress?.Report(resultProgress);

                // 認証成功時、停止中・エラー状態であれば自動再購読を試みる
                if (_subscriptionService.State == SubscriptionState.Stopped ||
                    _subscriptionService.State == SubscriptionState.Error)
                {
                    await _loggingService.WriteAsync("[AUTH] Restarting subscription service after successful login...").ConfigureAwait(false);
                    try
                    {
                        _subscriptionService.Start();
                    }
                    catch (Exception ex)
                    {
                        await _loggingService.WriteAsync($"[AUTH] Failed to restart subscription service: {ex.Message}").ConfigureAwait(false);
                    }
                }

                return resultProgress;
            }

            string errDetail = stderrOutput.ToString().Trim();
            if (string.IsNullOrEmpty(errDetail))
            {
                errDetail = $"Exit code: {process.ExitCode}";
            }

            string sanitizedError = SanitizeLogMessage(errDetail);
            await _loggingService.WriteAsync($"[AUTH] mcp-gateway login failed (exit code {process.ExitCode}): {sanitizedError}").ConfigureAwait(false);
            resultProgress.Stage = GatewayAuthStage.Failed;
            resultProgress.ErrorMessage = $"認証プロセスがエラーで終了しました ({sanitizedError})";
            progress?.Report(resultProgress);
            return resultProgress;
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);

            if (cancellationToken.IsCancellationRequested)
            {
                await _loggingService.WriteAsync("[AUTH] mcp-gateway login cancelled by user.").ConfigureAwait(false);
                resultProgress.Stage = GatewayAuthStage.Cancelled;
                resultProgress.ErrorMessage = "ユーザーによってログインがキャンセルされました。";
            }
            else
            {
                await _loggingService.WriteAsync("[AUTH] mcp-gateway login timed out.").ConfigureAwait(false);
                resultProgress.Stage = GatewayAuthStage.Timeout;
                resultProgress.ErrorMessage = "ログイン操作がタイムアウトしました。";
            }

            progress?.Report(resultProgress);
            return resultProgress;
        }
        catch (Exception ex)
        {
            await _loggingService.WriteAsync($"[AUTH] Unexpected error during login: {SanitizeLogMessage(ex.Message)}").ConfigureAwait(false);
            resultProgress.Stage = GatewayAuthStage.Failed;
            resultProgress.ErrorMessage = $"ログイン起動中に予期しないエラーが発生しました: {SanitizeLogMessage(ex.Message)}";
            progress?.Report(resultProgress);
            return resultProgress;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void TryKillProcess(IProcessInstance? process)
    {
        if (process == null)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // プロセスが既に終了している場合やアクセス権限例外を抑止
        }
    }

    private async Task ProcessOutputLineAsync(
        string line,
        GatewayAuthProgress resultProgress,
        IProgress<GatewayAuthProgress>? progress,
        AuthState state)
    {
        string? urlToOpen = null;

        lock (_outputLock)
        {
            Match urlMatch = _urlRegex.Match(line);
            if (urlMatch.Success && state.DetectedUrl == null)
            {
                state.DetectedUrl = urlMatch.Value;
                resultProgress.VerificationUrl = state.DetectedUrl;

                Match queryCodeMatch = _queryUserCodeRegex.Match(state.DetectedUrl);
                if (queryCodeMatch.Success && state.DetectedUserCode == null)
                {
                    state.DetectedUserCode = queryCodeMatch.Groups[1].Value;
                    resultProgress.UserCode = state.DetectedUserCode;
                }
            }

            if (state.DetectedUserCode == null)
            {
                Match codeMatch = _userCodeRegex.Match(line);
                if (codeMatch.Success)
                {
                    state.DetectedUserCode = codeMatch.Groups[1].Value;
                    resultProgress.UserCode = state.DetectedUserCode;
                }
            }

            if (state.DetectedUrl != null)
            {
                resultProgress.Stage = GatewayAuthStage.WaitingForUser;

                if (!state.HasLaunchedBrowser)
                {
                    state.HasLaunchedBrowser = true;
                    urlToOpen = state.DetectedUrl;
                }

                progress?.Report(resultProgress);
            }
        }

        if (urlToOpen != null)
        {
            await _loggingService.WriteAsync($"[AUTH] Verification URL detected: {urlToOpen}").ConfigureAwait(false);
            bool openSuccess = _browserLauncher.OpenUrl(urlToOpen);
            if (!openSuccess)
            {
                lock (_outputLock)
                {
                    resultProgress.BrowserLaunchFailed = true;
                    progress?.Report(resultProgress);
                }

                await _loggingService.WriteAsync("[AUTH] Failed to open default browser automatically for verification URL.").ConfigureAwait(false);
            }
        }
    }
}
