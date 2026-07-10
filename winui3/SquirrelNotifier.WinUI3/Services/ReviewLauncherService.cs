// <copyright file="ReviewLauncherService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

internal sealed class ReviewLauncherService : IReviewLauncherService
{
    private readonly SettingsService _settingsService;
    private readonly LoggingService _loggingService;
    private readonly IProcessRunner _processRunner;
    private readonly object _lock = new();

    private bool _isRunning;
    private IProcessInstance? _activeProcess;
    private CancellationTokenSource? _activeCts;

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _isRunning;
            }
        }
    }

    public ReviewLauncherService(
        SettingsService settingsService,
        LoggingService loggingService,
        IProcessRunner? processRunner = null)
    {
        _settingsService = settingsService;
        _loggingService = loggingService;
        _processRunner = processRunner ?? new ProcessRunner();
    }

    public async Task<LauncherResult> LaunchAsync(ReviewEvent reviewEvent, LauncherRole role, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reviewEvent);

        lock (_lock)
        {
            if (_isRunning)
            {
                return new LauncherResult
                {
                    Success = false,
                    ErrorMessage = "A review action is already running.",
                };
            }

            _isRunning = true;
        }

        await LogAsync($"User requested review execution for PR: {reviewEvent.Repository}#{reviewEvent.PrNumber} (role={role})").ConfigureAwait(false);

        AppSettings settings = _settingsService.Settings;
        (string commandPath, string argumentsTemplate) = ResolveLauncherSlot(settings, role);
        int timeoutMs = settings.LauncherTimeoutMs;

        _activeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeCts.CancelAfter(timeoutMs);
        CancellationToken combinedToken = _activeCts.Token;

        try
        {
            string resolvedPath = SettingsService.ResolveCommandPath(commandPath);
            if (string.IsNullOrWhiteSpace(resolvedPath))
            {
                throw new InvalidOperationException("Launcher command path is empty or invalid.");
            }

            var psi = new ProcessStartInfo
            {
                FileName = resolvedPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };

            // Build and add safe arguments
            System.Collections.Generic.List<string> args = LauncherArgumentBuilder.BuildArguments(argumentsTemplate, reviewEvent);
            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            await LogAsync($"Launching: {commandPath} with safe arguments").ConfigureAwait(false);

            lock (_lock)
            {
                combinedToken.ThrowIfCancellationRequested();

                _activeProcess = _processRunner.Start(psi);
            }

            // codex exec 等、スキル呼び出し機構を持たずプロンプト全文を引数で受け取るエージェントは
            // stdin の EOF を待って停止することがあるため、標準入力を即座に閉じて EOF を通知する.
            _activeProcess.StandardInput.Close();

            // Start reading stdout / stderr as tasks to avoid deadlock
            Task<string> stdoutTask = _activeProcess.StandardOutput.ReadToEndAsync(combinedToken);
            Task<string> stderrTask = _activeProcess.StandardError.ReadToEndAsync(combinedToken);

            await _activeProcess.WaitForExitAsync(combinedToken).ConfigureAwait(false);

            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);

            int exitCode = _activeProcess.ExitCode;

            await LogAsync($"Review process finished. ExitCode={exitCode}").ConfigureAwait(false);

            return new LauncherResult
            {
                Success = exitCode == 0,
                ExitCode = exitCode,
                Stdout = stdout,
                Stderr = stderr,
            };
        }
        catch (OperationCanceledException)
        {
            string reason = cancellationToken.IsCancellationRequested ? "cancelled by user" : "timed out";
            await LogAsync($"Review process was {reason}.").ConfigureAwait(false);

            KillActiveProcess();

            return new LauncherResult
            {
                Success = false,
                ErrorMessage = $"Review process was {reason}.",
            };
        }
        catch (Exception ex)
        {
            await LogAsync($"Failed to run review process: {ex.Message}").ConfigureAwait(false);

            KillActiveProcess();

            return new LauncherResult
            {
                Success = false,
                ErrorMessage = ex.Message,
            };
        }
        finally
        {
            lock (_lock)
            {
                _activeProcess?.Dispose();
                _activeProcess = null;
                _activeCts?.Dispose();
                _activeCts = null;
                _isRunning = false;
            }
        }
    }

    public void Cancel()
    {
        lock (_lock)
        {
            _activeCts?.Cancel();
            KillActiveProcess();
        }
    }

    // 起動せずに、実際に LaunchAsync が実行するのと同じスロット選択・引数展開を適用した
    // コマンド文字列を組み立てる（クリップボードコピー用）。コマンドパスは
    // ResolveCommandPath で解決した絶対パスではなく、設定値そのもの（例: "claude"）を使う。
    // ユーザーが自分のターミナルへ貼り付けて実行する用途では、そちらの方が可搬性が高く分かりやすいため。
    public string BuildCommandLine(ReviewEvent reviewEvent, LauncherRole role)
    {
        ArgumentNullException.ThrowIfNull(reviewEvent);

        AppSettings settings = _settingsService.Settings;
        (string commandPath, string argumentsTemplate) = ResolveLauncherSlot(settings, role);
        System.Collections.Generic.List<string> args = LauncherArgumentBuilder.BuildArguments(argumentsTemplate, reviewEvent);

        return CommandLineFormatter.Format(commandPath, args);
    }

    // スロットはユーザーが押したアクションのロールだけで決まる（#127）
    private static (string CommandPath, string ArgumentsTemplate) ResolveLauncherSlot(AppSettings settings, LauncherRole role)
    {
        return role == LauncherRole.Reviewer
            ? (settings.ReviewerLauncherCommandPath, settings.ReviewerLauncherArguments)
            : (settings.ReviewedLauncherCommandPath, settings.ReviewedLauncherArguments);
    }

    private void KillActiveProcess()
    {
        if (_activeProcess != null)
        {
            try
            {
                _activeProcess.Kill(entireProcessTree: true);
            }
            catch (Exception logEx)
            {
                // Ignore process kill errors
                _ = LogAsync($"Failed to kill process tree: {logEx.Message}");
            }
        }
    }

    private async Task LogAsync(string message)
    {
        await _loggingService.WriteAsync(message).ConfigureAwait(false);
    }
}
