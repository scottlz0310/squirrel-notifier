// <copyright file="EnqueueReviewService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// PR URL / <c>owner/repo#number</c> の手動登録を、mcp-resource-subscriber の
/// <c>call --tool enqueue_review</c> 経由で thread-owl の review queue へ enqueue する。
/// queue をバイパスして直接ランチャーを起動する経路は作らない（重複排除・通知記録を通る
/// 正規経路を維持するため）.
/// </summary>
internal sealed class EnqueueReviewService : IEnqueueReviewService
{
    private const string _toolName = "enqueue_review";
    private const int _callTimeoutMs = 30000;

    // call サブコマンドは mcp-resource-subscriber v0.4.0 で追加された。これより古いバージョンは
    // "call" を positional 引数として認識せず、既定の subscribe モード（test://review/status への
    // 購読）にフォールバックしてしまい、失敗の原因が分かりにくい形で誤動作する。
    private static readonly Version _minimumSubscriberVersion = new(0, 4, 0);
    private static readonly Regex _versionRegex = new(@"v(\d+)\.(\d+)\.(\d+)", RegexOptions.Compiled);

    private readonly SettingsService _settingsService;
    private readonly LoggingService _loggingService;
    private readonly IProcessRunner _processRunner;
    private readonly SecretMasker _secretMasker;

    public EnqueueReviewService(
        SettingsService settingsService,
        LoggingService loggingService,
        IProcessRunner? processRunner = null,
        SecretMasker? secretMasker = null)
    {
        _settingsService = settingsService;
        _loggingService = loggingService;
        _processRunner = processRunner ?? new ProcessRunner();
        _secretMasker = secretMasker ?? SecretMasker.CreateDefault();
    }

    public async Task<EnqueueReviewResult> EnqueueAsync(PrReference reference, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);

        AppSettings settings = _settingsService.Settings;
        string resolvedPath = SettingsService.ResolveCommandPath(settings.SubscriberCommandPath);

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_callTimeoutMs);

        IProcessInstance? process = null;
        try
        {
            string? versionError = await CheckSubscriberVersionAsync(resolvedPath, cts.Token).ConfigureAwait(false);
            if (versionError != null)
            {
                return new EnqueueReviewResult { Success = false, ErrorMessage = versionError };
            }

            string argsJson = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["owner"] = reference.Owner,
                ["repo"] = reference.Repo,
                ["prNumber"] = reference.PrNumber,
                ["reason"] = reason,
            });

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

            // "call" は先頭 positional 引数として認識される必要があるため、他の引数より先に追加する。
            psi.ArgumentList.Add("call");

            // 購読側（McpSubscriptionService）と同じ固定引数（例: --skip-resource-list-check 等）を
            // 手動 enqueue でも引き継ぐ。引き継がないと、購読が成功する環境でも手動開始だけ
            // 認証・接続条件を満たせず失敗する。
            if (!string.IsNullOrWhiteSpace(settings.SubscriberArguments))
            {
                foreach (string arg in McpSubscriptionService.ParseArguments(settings.SubscriberArguments))
                {
                    psi.ArgumentList.Add(arg);
                }
            }

            psi.ArgumentList.Add("--url");
            psi.ArgumentList.Add(settings.GatewayUrl);
            psi.ArgumentList.Add("--tool");
            psi.ArgumentList.Add(_toolName);
            psi.ArgumentList.Add("--args");
            psi.ArgumentList.Add(argsJson);
            psi.ArgumentList.Add("--json");

            await LogAsync($"Enqueuing review: {reference.Owner}/{reference.Repo}#{reference.PrNumber} (reason={reason})").ConfigureAwait(false);

            process = _processRunner.Start(psi);

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);
            int exitCode = process.ExitCode;

            await LogAsync($"enqueue_review call finished. ExitCode={exitCode}").ConfigureAwait(false);

            return BuildResult(exitCode, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);

            string reasonText = cancellationToken.IsCancellationRequested ? "キャンセルされました" : "タイムアウトしました";
            await LogAsync("enqueue_review call was cancelled or timed out.").ConfigureAwait(false);

            return new EnqueueReviewResult
            {
                Success = false,
                ErrorMessage = $"レビュー登録が{reasonText}。",
            };
        }
        catch (Exception ex)
        {
            KillProcess(process);
            await LogAsync($"Failed to call enqueue_review: {ex.Message}").ConfigureAwait(false);

            return new EnqueueReviewResult
            {
                Success = false,
                ErrorMessage = $"レビュー登録の呼び出しに失敗しました: {ex.Message}",
            };
        }
        finally
        {
            process?.Dispose();
        }
    }

    // mcp-resource-subscriber の --version 出力（"<name> vX.Y.Z"）を確認し、call サブコマンドが
    // 存在しない古いバージョンを事前に検出する。問題なければ null、問題があれば案内メッセージを返す。
    private async Task<string?> CheckSubscriberVersionAsync(string resolvedPath, CancellationToken cancellationToken)
    {
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
        psi.ArgumentList.Add("--version");

        IProcessInstance? process = null;
        try
        {
            process = _processRunner.Start(psi);

            // stderr を読み進めないと、subscriber がパイプバッファを超える出力をした時点で
            // 子プロセスが書き込みでブロックし WaitForExitAsync が返らなくなる（#201）。
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);

            Match match = _versionRegex.Match(stdout);
            if (!match.Success)
            {
                string stdoutDetail = ProcessOutputSummarizer.Summarize(stdout, _secretMasker);
                string stderrDetail = ProcessOutputSummarizer.Summarize(stderr, _secretMasker);
                string outputDetail = string.IsNullOrEmpty(stderrDetail)
                    ? $"\"{stdoutDetail}\""
                    : $"\"{stdoutDetail}\", stderr: \"{stderrDetail}\"";
                return $"mcp-resource-subscriber のバージョンを確認できませんでした（--version の出力: {outputDetail}）。call サブコマンドには v{_minimumSubscriberVersion} 以上が必要です。";
            }

            var detected = new Version(
                int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));

            if (detected < _minimumSubscriberVersion)
            {
                return $"mcp-resource-subscriber のバージョンが古いため、call サブコマンドを実行できません（検出: v{detected}, 必要: v{_minimumSubscriberVersion} 以上）。mcp-resource-subscriber を更新してください。";
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);
            throw;
        }
        catch (Exception ex)
        {
            return $"mcp-resource-subscriber のバージョン確認に失敗しました: {ex.Message}";
        }
        finally
        {
            process?.Dispose();
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
            // Process already exited before Kill() could run
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // OS-level failure to terminate the process; nothing more we can do here
        }
    }

    // mcp-resource-subscriber の call モードの exit code は成否・エラー種別を区別する:
    // 0 = 成功, 1 = tool エラー（allowlist 外を含む）, 2 = 認証エラー, 3 = 通信/使用方法エラー。
    private static EnqueueReviewResult BuildResult(int exitCode, string stdout, string stderr)
    {
        CallToolResult? result = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(stdout))
            {
                result = JsonSerializer.Deserialize<CallToolResult>(stdout);
            }
        }
        catch (JsonException)
        {
            // fall through: 生の stdout/stderr を使って処理を続ける
        }

        if (exitCode == 0)
        {
            return new EnqueueReviewResult { Success = true, ExitCode = exitCode };
        }

        string detail = result?.ExtractText() is string text && !string.IsNullOrWhiteSpace(text)
            ? text
            : (!string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim());

        return exitCode switch
        {
            1 => new EnqueueReviewResult
            {
                Success = false,
                ExitCode = exitCode,
                ErrorMessage = $"レビュー登録がツールエラーで拒否されました（リポジトリが allowlist 外の可能性があります）: {detail}",
            },
            2 => new EnqueueReviewResult
            {
                Success = false,
                ExitCode = exitCode,
                IsAuthenticationRequired = true,
                ErrorMessage = BuildAuthErrorMessage(result?.ErrorCode, detail),
            },
            _ => new EnqueueReviewResult
            {
                Success = false,
                ExitCode = exitCode,
                ErrorMessage = $"通信エラーが発生しました。Gateway URL や mcp-resource-subscriber の設定を確認してください: {detail}",
            },
        };
    }

    // AUTH_FAILED（明示指定した MCP_PROBE_AUTH_TOKEN が無効な場合など）は --login のトークン
    // キャッシュより優先されるため、再ログインでは解消しない。McpSubscriptionService.GetErrorInfo
    // と同様に、エラー種別に応じて案内を出し分ける。
    private static string BuildAuthErrorMessage(string? errorCode, string detail)
    {
        if (string.Equals(errorCode, "AUTH_FAILED", StringComparison.OrdinalIgnoreCase))
        {
            return $"mcp-gateway への認証に失敗しました。MCP_PROBE_AUTH_TOKEN を指定している場合は、そのトークンが有効か確認してください（明示的なトークンは --login のキャッシュより優先されるため、再ログインだけでは解消しません）。({detail})";
        }

        return $"mcp-gateway への認証が必要です。mcp-resource-subscriber の --login を実行して再認証してください。({detail})";
    }

    private async Task LogAsync(string message)
    {
        await _loggingService.WriteAsync(message).ConfigureAwait(false);
    }
}
