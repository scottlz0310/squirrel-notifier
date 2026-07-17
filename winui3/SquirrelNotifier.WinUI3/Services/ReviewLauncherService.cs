// <copyright file="ReviewLauncherService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
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
    private readonly TimeProvider _timeProvider;
    private readonly LauncherWorkingDirectoryResolver _workingDirectoryResolver;
    private readonly SecretMasker _secretMasker;
    private readonly object _lock = new();

    private bool _isRunning;
    private bool _cancelRequested;
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
        IProcessRunner? processRunner = null,
        TimeProvider? timeProvider = null,
        LauncherWorkingDirectoryResolver? workingDirectoryResolver = null,
        SecretMasker? secretMasker = null)
    {
        _settingsService = settingsService;
        _loggingService = loggingService;
        _processRunner = processRunner ?? new ProcessRunner();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _workingDirectoryResolver = workingDirectoryResolver ?? new LauncherWorkingDirectoryResolver(settingsService);
        _secretMasker = secretMasker ?? SecretMasker.CreateDefault();
    }

    public async Task<LauncherResult> LaunchAsync(ReviewEvent reviewEvent, LauncherRole role, CancellationToken cancellationToken)
    {
        AgentExecutionSession session = StartSession(reviewEvent, role, cancellationToken);
        return await session.Completion.ConfigureAwait(false);
    }

    // 実行を開始し、イベント購読用のセッションを即座に返す（#143）。
    // 実行結果は session.Completion で待機できる。LaunchAsync はこのメソッドの薄いラッパー.
    public AgentExecutionSession StartSession(ReviewEvent reviewEvent, LauncherRole role, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reviewEvent);

        var session = new AgentExecutionSession(_timeProvider);
        CancellationTokenSource cts;

        lock (_lock)
        {
            if (_isRunning)
            {
                session.Complete(AgentExecutionOutcome.Failed, new LauncherResult
                {
                    Success = false,
                    ErrorMessage = "A review action is already running.",
                });
                return session;
            }

            _isRunning = true;
            _cancelRequested = false;

            // StartSession 直後（fire-and-forget の RunSessionAsync がプロセスを起動する前）の
            // Cancel() が無視されないよう、CTS はここで生成して _activeCts に公開しておく。
            // Cancel() は同じ lock 内で _activeCts を cancel するため、StartSession が戻った
            // 時点以降のキャンセルは必ず combinedToken（プロセス起動前の
            // ThrowIfCancellationRequested）に届く.
            cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeCts = cts;
        }

        // RunSessionAsync は例外をすべて terminal event に変換するため fire-and-forget で安全
        _ = RunSessionAsync(session, reviewEvent, role, cts, cancellationToken);
        return session;
    }

    public void Cancel()
    {
        lock (_lock)
        {
            // Cancel() は内部 CTS だけを cancel し、呼び出し元の cancellationToken には
            // 伝播しない。timeout（内部 CTS の CancelAfter）と区別して terminal event を
            // Cancelled に分類できるよう、手動キャンセルの発生をフラグで記録する.
            _cancelRequested = true;
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
        List<string> args = LauncherArgumentBuilder.BuildArguments(argumentsTemplate, reviewEvent);

        return CommandLineFormatter.Format(commandPath, args);
    }

    private async Task RunSessionAsync(AgentExecutionSession session, ReviewEvent reviewEvent, LauncherRole role, CancellationTokenSource cts, CancellationToken cancellationToken)
    {
        // StartSession の fire-and-forget 呼び出し元へ確実に制御を返すため、最初に一度
        // スケジューラを経由する。LogAsync が書き込む File.AppendAllTextAsync は OS
        // キャッシュにヒットすると同期的に完了することがあり、その場合コンパイラの
        // 完了済みタスク最適化により、これ以降の同期区間が StartSession の呼び出しスレッド上で
        // プロセス起動直前まで進んでしまう。これにより「StartSession 直後の Cancel()」（開始前
        // レース、#143）がプロセス起動チェックに間に合わないことがあった.
        await Task.Yield();

        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;

        try
        {
            await LogAsync($"User requested review execution for PR: {reviewEvent.Repository}#{reviewEvent.PrNumber} (role={role})").ConfigureAwait(false);

            AppSettings settings = _settingsService.Settings;
            (string commandPath, string argumentsTemplate) = ResolveLauncherSlot(settings, role);
            int timeoutMs = settings.LauncherTimeoutMs;

            // CTS 自体は StartSession が生成済み（開始前キャンセルのレース対策）。
            // timeout の起点はここ（設定読み取り後）から.
            cts.CancelAfter(timeoutMs);
            CancellationToken combinedToken = cts.Token;

            string resolvedPath = SettingsService.ResolveCommandPath(commandPath);
            if (string.IsNullOrWhiteSpace(resolvedPath))
            {
                throw new InvalidOperationException("Launcher command path is empty or invalid.");
            }

            string workingDirectory = _workingDirectoryResolver.Resolve(reviewEvent, role);

            // .cmd / .bat シムの cmd.exe ラップと引数の安全な受け渡しは共通 factory に委ねる（#186）
            List<string> args = LauncherArgumentBuilder.BuildArguments(argumentsTemplate, reviewEvent);
            ProcessStartInfo psi = AgentProcessStartInfoFactory.Create(resolvedPath, args);
            psi.WorkingDirectory = workingDirectory;
            psi.StandardOutputEncoding = System.Text.Encoding.UTF8;
            psi.StandardErrorEncoding = System.Text.Encoding.UTF8;

            string executableKind = Path.GetExtension(resolvedPath).ToUpperInvariant() switch
            {
                ".CMD" => "cmd",
                ".BAT" => "bat",
                ".PS1" => "powershell-script",
                _ => "native",
            };
            await LogAsync(
                $"Launching: {commandPath} with safe arguments. WorkingDirectory={workingDirectory}; ResolvedExecutable={resolvedPath}; ExecutableKind={executableKind}")
                .ConfigureAwait(false);

            lock (_lock)
            {
                combinedToken.ThrowIfCancellationRequested();

                _activeProcess = _processRunner.Start(psi);
            }

            // codex exec 等、スキル呼び出し機構を持たずプロンプト全文を引数で受け取るエージェントは
            // stdin の EOF を待って停止することがあるため、標準入力を即座に閉じて EOF を通知する.
            _activeProcess.StandardInput.Close();

            // 実行中の逐次配信（#143）: 終了後一括の ReadToEndAsync ではなく行単位で読み取り、
            // stdout / stderr の各ストリーム内の順序を維持したままセッションへ流す。
            // WaitForExitAsync と並行して読み進めることでパイプ詰まりによる deadlock も回避する.
            stdoutTask = PumpStdoutAsync(_activeProcess.StandardOutput, session, combinedToken);
            stderrTask = PumpStderrAsync(_activeProcess.StandardError, session, combinedToken);

            await _activeProcess.WaitForExitAsync(combinedToken).ConfigureAwait(false);

            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);

            int exitCode = _activeProcess.ExitCode;

            await LogAsync($"Review process finished. ExitCode={exitCode}; WorkingDirectory={workingDirectory}").ConfigureAwait(false);
            if (exitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
            {
                await LogAsync($"Review process stderr summary: {BuildStderrSummary(stderr)}").ConfigureAwait(false);
            }

            session.Complete(
                exitCode == 0 ? AgentExecutionOutcome.Succeeded : AgentExecutionOutcome.Failed,
                new LauncherResult
                {
                    Success = exitCode == 0,
                    ExitCode = exitCode,
                    Stdout = stdout,
                    Stderr = stderr,
                });
        }
        catch (OperationCanceledException)
        {
            bool cancelledByUser;
            lock (_lock)
            {
                // 呼び出し元トークン経由（cancellationToken）と Cancel() 経由（_cancelRequested）の
                // どちらも user-cancel。どちらでもなければ内部 CTS の CancelAfter による timeout.
                cancelledByUser = cancellationToken.IsCancellationRequested || _cancelRequested;
            }

            string reason = cancelledByUser ? "cancelled by user" : "timed out";
            await LogAsync($"Review process was {reason}.").ConfigureAwait(false);

            KillActiveProcess();
            await WaitForPumpsAsync(stdoutTask, stderrTask).ConfigureAwait(false);

            session.Complete(
                cancelledByUser ? AgentExecutionOutcome.Cancelled : AgentExecutionOutcome.TimedOut,
                new LauncherResult
                {
                    Success = false,
                    ErrorMessage = $"Review process was {reason}.",
                });
        }
        catch (Exception ex)
        {
            await LogAsync($"Failed to run review process: {ex.Message}").ConfigureAwait(false);

            KillActiveProcess();
            await WaitForPumpsAsync(stdoutTask, stderrTask).ConfigureAwait(false);

            session.Complete(AgentExecutionOutcome.Failed, new LauncherResult
            {
                Success = false,
                ErrorMessage = ex.Message,
            });
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

    private static async Task<string> PumpStdoutAsync(StreamReader reader, AgentExecutionSession session, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is string line)
        {
            lines.Add(line);

            if (ProgressEventParser.TryParse(line, out AgentProgressEvent? progress))
            {
                session.PublishProgress(progress!);
            }
            else
            {
                // マーカー不一致・malformed JSON・未知 schemaVersion は通常ログとして流す（#143 AC）
                session.PublishStdout(line);
            }
        }

        return string.Join('\n', lines);
    }

    private static async Task<string> PumpStderrAsync(StreamReader reader, AgentExecutionSession session, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is string line)
        {
            lines.Add(line);
            session.PublishStderr(line);
        }

        return string.Join('\n', lines);
    }

    // キャンセル・タイムアウト・例外時に reader task を残存させないための後始末。
    // プロセス kill / トークンキャンセルに伴う読み取り側の例外は正常な終了経路として扱う.
    private static async Task WaitForPumpsAsync(Task<string>? stdoutTask, Task<string>? stderrTask)
    {
        foreach (Task<string>? pump in new[] { stdoutTask, stderrTask })
        {
            if (pump == null)
            {
                continue;
            }

            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private string BuildStderrSummary(string stderr)
    {
        const int maxLength = 500;
        string summary = string.Join(" | ", stderr
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(3)
            .Select(line => _secretMasker.Mask(AnsiControlSanitizer.Sanitize(line))));
        return summary.Length <= maxLength ? summary : summary[..maxLength];
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
