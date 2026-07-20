// <copyright file="GatewayAuthService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Diagnostics;
using System.IO;
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
        var resultProgress = new GatewayAuthProgress
        {
            Stage = GatewayAuthStage.Starting,
        };

        progress?.Report(resultProgress);

        AppSettings settings = _settingsService.Settings;
        string resolvedPath = SettingsService.ResolveCommandPath(settings.SubscriberCommandPath);
        string gatewayUrl = settings.GatewayUrl;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_loginTimeoutMs);

        await _loggingService.WriteAsync($"[AUTH] Starting mcp-gateway login process: {resolvedPath} --login").ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName = resolvedPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
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

            bool hasLaunchedBrowser = false;
            string? detectedUrl = null;
            string? detectedUserCode = null;
            var stderrOutput = new System.Text.StringBuilder();

            Task readStdoutTask = Task.Run(async () =>
            {
                while (!process.StandardOutput.EndOfStream)
                {
                    string? line = await process.StandardOutput.ReadLineAsync(cts.Token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    ProcessOutputLine(
                        line,
                        ref detectedUrl,
                        ref detectedUserCode,
                        ref hasLaunchedBrowser,
                        resultProgress,
                        progress);
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

                    ProcessOutputLine(
                        line,
                        ref detectedUrl,
                        ref detectedUserCode,
                        ref hasLaunchedBrowser,
                        resultProgress,
                        progress);
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
                    _ = _subscriptionService.StartAsync();
                }

                return resultProgress;
            }

            string errDetail = stderrOutput.ToString().Trim();
            if (string.IsNullOrEmpty(errDetail))
            {
                errDetail = $"Exit code: {process.ExitCode}";
            }

            await _loggingService.WriteAsync($"[AUTH] mcp-gateway login failed (exit code {process.ExitCode}): {SanitizeLogMessage(errDetail)}").ConfigureAwait(false);
            resultProgress.Stage = GatewayAuthStage.Failed;
            resultProgress.ErrorMessage = $"認証プロセスがエラーで終了しました ({errDetail})";
            progress?.Report(resultProgress);
            return resultProgress;
        }
        catch (OperationCanceledException)
        {
            process?.Kill(entireProcessTree: true);

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
            await _loggingService.WriteAsync($"[AUTH] Unexpected error during login: {ex.Message}").ConfigureAwait(false);
            resultProgress.Stage = GatewayAuthStage.Failed;
            resultProgress.ErrorMessage = $"ログイン起動中に予期しないエラーが発生しました: {ex.Message}";
            progress?.Report(resultProgress);
            return resultProgress;
        }
        finally
        {
            process?.Dispose();
        }
    }

    internal static string SanitizeLogMessage(string message)
    {
        // ログに token や secret や Authorization ヘッダーが混入しないよう簡易マスキング
        string sanitized = Regex.Replace(message, @"(bearer\s+|token[=:]\s*)[A-Za-z0-9_\-\.]+", "$1***", RegexOptions.IgnoreCase);
        return sanitized;
    }

    private void ProcessOutputLine(
        string line,
        ref string? detectedUrl,
        ref string? detectedUserCode,
        ref bool hasLaunchedBrowser,
        GatewayAuthProgress resultProgress,
        IProgress<GatewayAuthProgress>? progress)
    {
        Match urlMatch = _urlRegex.Match(line);
        if (urlMatch.Success && detectedUrl == null)
        {
            detectedUrl = urlMatch.Value;
            resultProgress.VerificationUrl = detectedUrl;

            Match queryCodeMatch = _queryUserCodeRegex.Match(detectedUrl);
            if (queryCodeMatch.Success && detectedUserCode == null)
            {
                detectedUserCode = queryCodeMatch.Groups[1].Value;
                resultProgress.UserCode = detectedUserCode;
            }
        }

        if (detectedUserCode == null)
        {
            Match codeMatch = _userCodeRegex.Match(line);
            if (codeMatch.Success)
            {
                detectedUserCode = codeMatch.Groups[1].Value;
                resultProgress.UserCode = detectedUserCode;
            }
        }

        if (detectedUrl != null)
        {
            resultProgress.Stage = GatewayAuthStage.WaitingForUser;

            if (!hasLaunchedBrowser)
            {
                hasLaunchedBrowser = true;
                _ = _loggingService.WriteAsync($"[AUTH] Verification URL detected: {detectedUrl}");
                bool openSuccess = _browserLauncher.OpenUrl(detectedUrl);
                if (!openSuccess)
                {
                    resultProgress.BrowserLaunchFailed = true;
                    _ = _loggingService.WriteAsync("[AUTH] Failed to open default browser automatically for verification URL.");
                }
            }

            progress?.Report(resultProgress);
        }
    }
}
