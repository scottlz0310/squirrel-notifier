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

    /// <summary>起動処理が進行中のため見送った（ボタン連打などによる再入）。自動起動では <see cref="SkippedBusy"/> として保留する.</summary>
    SkippedReentrant,

    /// <summary>別のレビューが実行中のため見送った.</summary>
    SkippedBusy,

    /// <summary>
    /// Auto-Pause（#147）中で override を確認できないため見送った。自動起動では解除後の
    /// 再評価まで保留する（#340）.
    /// </summary>
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
internal sealed record ReviewStartResult(
    ReviewStartStatus Status,
    ReviewStartLaunch? Launch,
    string? FailureMessage,
    string? HoldReason = null)
{
    public bool IsStarted => Status == ReviewStartStatus.Started;

    public static ReviewStartResult Skipped(ReviewStartStatus status) => new(status, null, null);

    /// <summary>
    /// 再評価まで保留したイベントの結果（#339/#340）.
    /// </summary>
    /// <param name="status">見送りの理由.</param>
    /// <param name="holdReason">保留した理由。通知に出す短い文言.</param>
    /// <returns>保留を表す結果.</returns>
    public static ReviewStartResult Held(ReviewStartStatus status, string holdReason)
        => new(status, null, null, holdReason);

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
    private readonly AutoPauseResumeScheduler _autoPauseResumeScheduler;
    private readonly LoggingService _loggingService;
    private readonly ReviewCycleCoordinator? _reviewCycleCoordinator;

    // Auto-Pause 確認ダイアログ等の await 中は IsRunning がまだ false のため、起動ボタンの
    // 連打で再入し ContentDialog の多重表示（WinUI3 では例外）になる。それを防ぐフラグ
    private bool _isStartPending;

    public ReviewStartCoordinator(
        IReviewLauncherService launcherService,
        SettingsService settingsService,
        RateLimitSnapshotService rateLimitSnapshotService,
        AutoPauseGate autoPauseGate,
        PendingReviewStartQueue pendingQueue,
        AutoPauseResumeScheduler autoPauseResumeScheduler,
        LoggingService loggingService,
        ReviewCycleCoordinator? reviewCycleCoordinator = null)
    {
        ArgumentNullException.ThrowIfNull(launcherService);
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(rateLimitSnapshotService);
        ArgumentNullException.ThrowIfNull(autoPauseGate);
        ArgumentNullException.ThrowIfNull(pendingQueue);
        ArgumentNullException.ThrowIfNull(autoPauseResumeScheduler);
        ArgumentNullException.ThrowIfNull(loggingService);

        _launcherService = launcherService;
        _settingsService = settingsService;
        _rateLimitSnapshotService = rateLimitSnapshotService;
        _autoPauseGate = autoPauseGate;
        _pendingQueue = pendingQueue;
        _autoPauseResumeScheduler = autoPauseResumeScheduler;
        _loggingService = loggingService;
        _reviewCycleCoordinator = reviewCycleCoordinator;
    }

    /// <summary>
    /// 起動処理を始めたが、セッションを起動せずに終わったときに発生する（確認ダイアログのキャンセル・
    /// 起動失敗・キャンセル例外）。その間に保留したイベントは実行終了（<see cref="IReviewLauncherService.RunCompleted"/>）を
    /// 待っても再評価されないため、再評価の契機に使う（#339）。起動処理を呼び出したスレッドで発生する。
    /// Auto-Pause を理由に保留した場合は、再評価しても同じ判定になるため発生しない（#340。解除は
    /// <see cref="AutoPauseGate.Released"/> と <see cref="AutoPauseResumeScheduler"/> が契機になる）.
    /// </summary>
    public event EventHandler? StartAbandoned;

    /// <summary>Gets a value indicating whether 起動処理が進行中、または実行中のレビューがあるか.</summary>
    public bool IsBusy => _isStartPending || _launcherService.IsRunning;

    /// <summary>
    /// 「レビュー自動開始」設定（#254）に従って reviewer を自動起動する。
    /// 起動を見送った場合はその理由を Recent activity へ残す。別のレビューが実行中で見送った場合は、
    /// 実行終了後に再評価するためイベントを保留する（#339）。Auto-Pause 中で見送った場合は
    /// <see cref="StartAsync"/> が解除後の再評価まで保留する（#340）.
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
            await HoldForBusyAsync(reviewEvent);
            return ReviewStartResult.Held(ReviewStartStatus.SkippedBusy, ReviewAutoStartPolicy.BusyHoldLabel);
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
            if (trigger == ReviewStartTrigger.Automatic)
            {
                // 自動起動の判定後、ログ書き込みの await 中に手動起動が始まった場合に到達する。
                // 実行中と同じく保留しないと、再評価中のイベントが起動されないまま失われる（#339）
                await HoldForBusyAsync(reviewEvent);
                return ReviewStartResult.Held(ReviewStartStatus.SkippedBusy, ReviewAutoStartPolicy.BusyHoldLabel);
            }

            return ReviewStartResult.Skipped(ReviewStartStatus.SkippedReentrant);
        }

        if (_launcherService.IsRunning)
        {
            if (trigger == ReviewStartTrigger.Automatic)
            {
                // 判定後にここへ到達するのは、判定と起動の間に別のレビューが始まった場合のみ
                await HoldForBusyAsync(reviewEvent);
                return ReviewStartResult.Held(ReviewStartStatus.SkippedBusy, ReviewAutoStartPolicy.BusyHoldLabel);
            }

            return ReviewStartResult.Skipped(ReviewStartStatus.SkippedBusy);
        }

        _isStartPending = true;

        // 起動せずに終わったときに再評価を促すか（#339）。Auto-Pause で保留した場合は再評価しても
        // 同じ判定になるため促さない（#340）
        bool raiseStartAbandoned = true;
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
                    raiseStartAbandoned = false;
                    await HoldForAutoPauseAsync(reviewEvent, pausedLimit);
                    return ReviewStartResult.Held(
                        ReviewStartStatus.SkippedAutoPaused, ReviewAutoStartPolicy.AutoPausedHoldLabel);
                }

                if (!await confirmAutoPauseOverrideAsync!(pausedLimit))
                {
                    return ReviewStartResult.Skipped(ReviewStartStatus.CancelledByUser);
                }
            }

            AgentExecutionSession session = _launcherService.StartSession(reviewEvent, role, CancellationToken.None);
            raiseStartAbandoned = false;
            if (role == LauncherRole.Reviewer)
            {
                // 手動起動でも、同じ PR のレビューを始めた時点で保留分を再評価する意味はなくなる
                _pendingQueue.RemovePullRequest(reviewEvent);
            }

            ReviewStartLaunch launch = new(session, viewModel, rateLimitGaugeViewModel, rateLimitSessionMonitor);
            if (role == LauncherRole.Reviewer && _reviewCycleCoordinator is not null)
            {
                await _reviewCycleCoordinator.MarkReviewerStartedAsync(reviewEvent, launch);
            }

            return ReviewStartResult.Launched(launch);
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
            if (raiseStartAbandoned)
            {
                StartAbandoned?.Invoke(this, EventArgs.Empty);
            }
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

    private Task HoldForBusyAsync(ReviewEvent reviewEvent)
        => HoldAsync(reviewEvent, $"{ReviewAutoStartPolicy.BusyReasonText}。実行終了後に自動起動します");

    // Auto-Pause は解除しても gate を再評価する契機が無いため、リセット時刻へ再評価を予約する（#340）
    private Task HoldForAutoPauseAsync(ReviewEvent reviewEvent, AutoPausedLimit pausedLimit)
    {
        _autoPauseResumeScheduler.Schedule(pausedLimit.ResetAt);
        return HoldAsync(reviewEvent, $"Auto-Pause 中のため（{pausedLimit.BuildReasonText()}）。解除後に自動起動します");
    }

    private Task HoldAsync(ReviewEvent reviewEvent, string reasonText)
        => _pendingQueue.AddOrReplace(reviewEvent) switch
        {
            PendingReviewStartChange.Added => _loggingService.WriteAsync(
                $"[Auto] {reviewEvent.PrCaption} のレビューを保留しました: {reasonText}（reason: {reviewEvent.Reason}）。"),
            PendingReviewStartChange.Replaced => _loggingService.WriteAsync(
                $"[Auto] 保留中の {reviewEvent.PrCaption} を新しいイベントで更新しました（reason: {reviewEvent.Reason}）。"),
            _ => Task.CompletedTask,
        };

    private Task LogAutoStartSkipAsync(ReviewEvent reviewEvent, string reason)
        => _loggingService.WriteAsync($"[Auto] {reviewEvent.PrCaption} のレビューを自動起動しませんでした: {reason}");
}
