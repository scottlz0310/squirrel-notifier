// <copyright file="ReviewStartCoordinator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.ViewModels;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>レビュー起動の結果（#264）.</summary>
internal enum ReviewStartStatus
{
    /// <summary>起動した.</summary>
    Started,

    /// <summary>起動処理が進行中のため見送った（ボタン連打などによる再入）.</summary>
    SkippedReentrant,

    /// <summary>別のレビューが実行中のため見送った.</summary>
    SkippedBusy,

    /// <summary>Auto-Pause（#147）中で override を確認できないため見送った.</summary>
    SkippedAutoPaused,

    /// <summary>「レビュー自動開始」設定が off のため見送った.</summary>
    SkippedDisabled,

    /// <summary>reviewer 側のアクションを伴わない reason のため見送った.</summary>
    SkippedUnsupportedReason,

    /// <summary>Auto-Pause override の確認でユーザーがキャンセルした.</summary>
    CancelledByUser,

    /// <summary>起動中に例外が発生した.</summary>
    Failed,
}

/// <summary>起動に成功したセッションと、ライブログウィンドウ（#144）が必要とする表示状態.</summary>
internal sealed record ReviewStartLaunch(
    AgentExecutionSession Session,
    AgentExecutionViewModel ViewModel,
    RateLimitGaugeViewModel RateLimitGaugeViewModel,
    RateLimitSessionMonitor RateLimitSessionMonitor);

/// <summary>
/// レビュー起動の結果。<see cref="Launch"/> が非 null のときだけウィンドウを開く。
/// 見送りの理由は <see cref="Status"/> で表現し、ダイアログを出すか・何も出さないかは呼び出し側が決める.
/// </summary>
internal sealed record ReviewStartResult(ReviewStartStatus Status, ReviewStartLaunch? Launch, string? FailureMessage)
{
    public bool IsStarted => Status == ReviewStartStatus.Started;

    public static ReviewStartResult Skipped(ReviewStartStatus status) => new(status, null, null);

    public static ReviewStartResult Launched(ReviewStartLaunch launch) => new(ReviewStartStatus.Started, launch, null);

    public static ReviewStartResult Failure(string message) => new(ReviewStartStatus.Failed, null, message);
}

/// <summary>
/// レビュー起動の手続きと分岐を担う（#264）。多重起動の抑止・Auto-Pause（#147）の評価・
/// snapshot 取得・セッション生成・自動起動（#254）の判定と記録をまとめて持ち、
/// ダイアログ表示とウィンドウ生成だけを呼び出し側に残す.
/// </summary>
/// <remarks>
/// <para>
/// 本クラスは ConfigureAwait(false) を使わない。<see cref="AutoPauseGate.Evaluate"/> は
/// <see cref="AutoPauseGate.StateChanged"/> を通じて UI の InfoBar 更新を呼ぶため、
/// 呼び出し元（UI スレッド）の同期コンテキストを維持する必要がある.
/// </para>
/// <para>起動中フラグを持つためスレッドセーフではない。UI スレッドからの利用を前提とする.</para>
/// </remarks>
internal sealed class ReviewStartCoordinator
{
    private readonly IReviewLauncherService _launcherService;
    private readonly SettingsService _settingsService;
    private readonly RateLimitSnapshotService _rateLimitSnapshotService;
    private readonly AutoPauseGate _autoPauseGate;
    private readonly PendingReviewStartQueue _pendingQueue;
    private readonly LoggingService _loggingService;

    // Auto-Pause 確認ダイアログ等の await 中は IsRunning がまだ false のため、起動ボタンの
    // 連打で再入し ContentDialog の多重表示（WinUI3 では例外）になる。それを防ぐフラグ
    private bool _isStartPending;

    public ReviewStartCoordinator(
        IReviewLauncherService launcherService,
        SettingsService settingsService,
        RateLimitSnapshotService rateLimitSnapshotService,
        AutoPauseGate autoPauseGate,
        PendingReviewStartQueue pendingQueue,
        LoggingService loggingService)
    {
        ArgumentNullException.ThrowIfNull(launcherService);
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(rateLimitSnapshotService);
        ArgumentNullException.ThrowIfNull(autoPauseGate);
        ArgumentNullException.ThrowIfNull(pendingQueue);
        ArgumentNullException.ThrowIfNull(loggingService);

        _launcherService = launcherService;
        _settingsService = settingsService;
        _rateLimitSnapshotService = rateLimitSnapshotService;
        _autoPauseGate = autoPauseGate;
        _pendingQueue = pendingQueue;
        _loggingService = loggingService;
    }

    /// <summary>Gets a value indicating whether 起動処理が進行中、または実行中のレビューがあるか.</summary>
    public bool IsBusy => _isStartPending || _launcherService.IsRunning;

    /// <summary>
    /// 「レビュー自動開始」設定（#254）に従って reviewer を自動起動する。
    /// 起動を見送った場合はその理由を Recent activity へ残す。別のレビューが実行中で見送った場合は、
    /// 実行終了後に再評価するためイベントを保留する（#339）.
    /// </summary>
    /// <param name="reviewEvent">受信したレビューイベント.</param>
    /// <returns>起動結果.</returns>
    public async Task<ReviewStartResult> TryStartAutomaticallyAsync(ReviewEvent reviewEvent)
    {
        ArgumentNullException.ThrowIfNull(reviewEvent);

        ReviewAutoStartOutcome outcome = ReviewAutoStartPolicy.Evaluate(
            _settingsService.Settings.AutoReviewStartEnabled,
            reviewEvent.Reason,
            IsBusy);

        if (outcome == ReviewAutoStartOutcome.SkippedBusy)
        {
            await HoldAsync(reviewEvent);
            return ReviewStartResult.Skipped(ReviewStartStatus.SkippedBusy);
        }

        if (ReviewAutoStartPolicy.DescribeSkipReason(outcome) is string skipReason)
        {
            await LogAutoStartSkipAsync(reviewEvent, skipReason);
        }

        if (outcome != ReviewAutoStartOutcome.Start)
        {
            return ReviewStartResult.Skipped(MapAutoStartSkip(outcome));
        }

        await _loggingService.WriteAsync(
            $"[Auto] {reviewEvent.PrCaption} のレビューを自動起動します（reason: {reviewEvent.Reason}）。");
        return await StartAsync(reviewEvent, LauncherRole.Reviewer, ReviewStartTrigger.Automatic);
    }

    /// <summary>
    /// レビューを起動する.
    /// </summary>
    /// <param name="reviewEvent">起動対象のレビューイベント.</param>
    /// <param name="role">使用する launcher スロット.</param>
    /// <param name="trigger">
    /// 起動の起点（#254）。<see cref="ReviewStartTrigger.Automatic"/> は無人で走るため、
    /// 応答されないダイアログを出さずログへ理由を残して見送る.
    /// </param>
    /// <param name="confirmAutoPauseOverrideAsync">
    /// Auto-Pause 中に手動起動を強行してよいかの確認。<see cref="ReviewStartTrigger.Manual"/> では必須.
    /// </param>
    /// <param name="cancellationToken">
    /// snapshot 取得を中断するためのトークン。キャンセルされた場合は起動失敗
    /// （<see cref="ReviewStartStatus.Failed"/>）に変えず <see cref="OperationCanceledException"/>
    /// を送出する（再入抑止は解除される、#333）.
    /// </param>
    /// <returns>起動結果.</returns>
    public async Task<ReviewStartResult> StartAsync(
        ReviewEvent reviewEvent,
        LauncherRole role,
        ReviewStartTrigger trigger,
        Func<AutoPausedLimit, Task<bool>>? confirmAutoPauseOverrideAsync = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reviewEvent);
        if (trigger == ReviewStartTrigger.Manual && confirmAutoPauseOverrideAsync is null)
        {
            throw new ArgumentNullException(
                nameof(confirmAutoPauseOverrideAsync),
                "手動起動では Auto-Pause override の確認手段が必要です。");
        }

        // 再入はダイアログを出さず黙って無視する — ここでダイアログを出すこと自体が
        // 多重表示例外の原因になるため（#147 レビュー指摘）
        if (_isStartPending)
        {
            return ReviewStartResult.Skipped(ReviewStartStatus.SkippedReentrant);
        }

        if (_launcherService.IsRunning)
        {
            if (trigger == ReviewStartTrigger.Automatic)
            {
                // 判定後にここへ到達するのは、判定と起動の間に別のレビューが始まった場合のみ
                await HoldAsync(reviewEvent);
            }

            return ReviewStartResult.Skipped(ReviewStartStatus.SkippedBusy);
        }

        _isStartPending = true;
        try
        {
            AppSettings settings = _settingsService.Settings;
            AgentExecutionViewModel viewModel = new(
                BuildSessionTitle(reviewEvent, role),
                settings.LiveLogAutoCloseEnabled,
                SecretMasker.CreateDefault(),
                _settingsService.ResolveLauncherProgressEventSupport(role));

            string? activeAgentId = _settingsService.ResolveLauncherRateLimitAgentId(role);
            TimeSpan freshnessThreshold = TimeSpan.FromMinutes(settings.RateLimitFreshnessThresholdMinutes);
            RateLimitGaugeViewModel rateLimitGaugeViewModel = new(freshnessThreshold);
            RateLimitSessionMonitor rateLimitSessionMonitor = new(
                _rateLimitSnapshotService,
                new RateLimitDeltaCalculator(),
                settings.RateLimitMonitoredAgentIds,
                activeAgentId,
                freshnessThreshold);
            IReadOnlyList<RateLimitSnapshot> startSnapshots = await rateLimitSessionMonitor.CaptureStartAsync(cancellationToken);
            rateLimitGaugeViewModel.Update(settings.RateLimitMonitoredAgentIds, startSnapshots, activeAgentId, []);

            // Auto-Pause gate（#147）: 起動する launcher スロットの agent が危険水域なら
            // 新規起動を拒否する。実行中プロセス・MCP subscription・queue には作用しない
            AutoPauseDecision autoPauseDecision = _autoPauseGate.Evaluate(activeAgentId, startSnapshots, freshnessThreshold);
            if (autoPauseDecision.Status == AutoPauseStatus.Paused)
            {
                AutoPausedLimit pausedLimit = autoPauseDecision.PausedLimit!;
                if (!ReviewAutoStartPolicy.AllowsAutoPauseOverridePrompt(trigger))
                {
                    await LogAutoStartSkipAsync(reviewEvent, $"Auto-Pause 中のため（{pausedLimit.BuildReasonText()}）");
                    return ReviewStartResult.Skipped(ReviewStartStatus.SkippedAutoPaused);
                }

                if (!await confirmAutoPauseOverrideAsync!(pausedLimit))
                {
                    return ReviewStartResult.Skipped(ReviewStartStatus.CancelledByUser);
                }
            }

            AgentExecutionSession session = _launcherService.StartSession(reviewEvent, role, CancellationToken.None);
            if (role == LauncherRole.Reviewer)
            {
                // 手動起動でも、同じ PR のレビューを始めた時点で保留分を再評価する意味はなくなる
                _pendingQueue.RemovePullRequest(reviewEvent);
            }

            return ReviewStartResult.Launched(
                new ReviewStartLaunch(session, viewModel, rateLimitGaugeViewModel, rateLimitSessionMonitor));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (trigger == ReviewStartTrigger.Automatic)
            {
                // 無人実行のため応答されないダイアログは出さず、Recent activity に原因を残す
                await _loggingService.WriteAsync(
                    $"[Auto] {reviewEvent.PrCaption} のレビュー自動起動が失敗しました: {ex.Message}");
            }

            return ReviewStartResult.Failure(ex.Message);
        }
        finally
        {
            // StartSession 成功後の同時実行抑止は _launcherService.IsRunning が担う
            _isStartPending = false;
        }
    }

    private static string BuildSessionTitle(ReviewEvent reviewEvent, LauncherRole role)
    {
        string roleLabel = role == LauncherRole.Reviewer ? "レビューする" : "レビューに対応";
        return $"{reviewEvent.Repository}#{reviewEvent.PrNumber}（{roleLabel}）";
    }

    private static ReviewStartStatus MapAutoStartSkip(ReviewAutoStartOutcome outcome)
        => outcome switch
        {
            ReviewAutoStartOutcome.SkippedDisabled => ReviewStartStatus.SkippedDisabled,
            ReviewAutoStartOutcome.SkippedUnsupportedReason => ReviewStartStatus.SkippedUnsupportedReason,
            _ => ReviewStartStatus.SkippedBusy,
        };

    private Task HoldAsync(ReviewEvent reviewEvent)
        => _pendingQueue.AddOrReplace(reviewEvent) switch
        {
            PendingReviewStartChange.Added => _loggingService.WriteAsync(
                $"[Auto] {reviewEvent.PrCaption} のレビューを保留しました: {ReviewAutoStartPolicy.BusyReasonText}。"
                + $"実行終了後に自動起動します（reason: {reviewEvent.Reason}）。"),
            PendingReviewStartChange.Replaced => _loggingService.WriteAsync(
                $"[Auto] 保留中の {reviewEvent.PrCaption} を新しいイベントで更新しました（reason: {reviewEvent.Reason}）。"),
            _ => Task.CompletedTask,
        };

    private Task LogAutoStartSkipAsync(ReviewEvent reviewEvent, string reason)
        => _loggingService.WriteAsync($"[Auto] {reviewEvent.PrCaption} のレビューを自動起動しませんでした: {reason}");
}
