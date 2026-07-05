// <copyright file="EnqueueReviewService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// PR URL / <c>owner/repo#number</c> の手動登録を、mcp-resource-subscriber の
/// <c>call --tool enqueue_review</c> 経由で thread-owl の review queue へ enqueue する。
/// queue をバイパスして直接ランチャーを起動する経路は作らない（重複排除・通知記録を通る
/// 正規経路を維持するため）。.
/// </summary>
internal sealed class EnqueueReviewService
{
    private const string _toolName = "enqueue_review";
    private const int _callTimeoutMs = 30000;

    private readonly SettingsService _settingsService;
    private readonly LoggingService _loggingService;
    private readonly IProcessRunner _processRunner;

    public EnqueueReviewService(
        SettingsService settingsService,
        LoggingService loggingService,
        IProcessRunner? processRunner = null)
    {
        _settingsService = settingsService;
        _loggingService = loggingService;
        _processRunner = processRunner ?? new ProcessRunner();
    }

    public async Task<EnqueueReviewResult> EnqueueAsync(PrReference reference, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);

        AppSettings settings = _settingsService.Settings;

        string argsJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["owner"] = reference.Owner,
            ["repo"] = reference.Repo,
            ["prNumber"] = reference.PrNumber,
            ["reason"] = reason,
        });

        var psi = new ProcessStartInfo
        {
            FileName = SettingsService.ResolveCommandPath(settings.SubscriberCommandPath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        psi.ArgumentList.Add("call");
        psi.ArgumentList.Add("--url");
        psi.ArgumentList.Add(settings.GatewayUrl);
        psi.ArgumentList.Add("--tool");
        psi.ArgumentList.Add(_toolName);
        psi.ArgumentList.Add("--args");
        psi.ArgumentList.Add(argsJson);
        psi.ArgumentList.Add("--json");

        await LogAsync($"Enqueuing review: {reference.Owner}/{reference.Repo}#{reference.PrNumber} (reason={reason})").ConfigureAwait(false);

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
                ErrorMessage = $"mcp-gateway への認証が必要です。mcp-resource-subscriber の --login を実行して再認証してください。({detail})",
            },
            _ => new EnqueueReviewResult
            {
                Success = false,
                ExitCode = exitCode,
                ErrorMessage = $"通信エラーが発生しました。Gateway URL や mcp-resource-subscriber の設定を確認してください: {detail}",
            },
        };
    }

    private async Task LogAsync(string message)
    {
        await _loggingService.WriteAsync(message).ConfigureAwait(false);
    }
}
