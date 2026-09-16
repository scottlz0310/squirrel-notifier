// <copyright file="ReviewEventProcessingCoordinator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// 受信したレビューイベントの保持、終了確認、自動起動の順序を管理する.
/// </summary>
internal sealed class ReviewEventProcessingCoordinator
{
    private readonly ReviewEventCollectionCoordinator _collectionCoordinator;
    private readonly ReviewEventCleanupCoordinator _cleanupCoordinator;
    private readonly PendingReviewStartQueue _pendingQueue;
    private readonly LoggingService _loggingService;
    private readonly Func<ReviewEvent, Task<ReviewStartResult>> _tryStartAutomaticallyAsync;
    private bool _isProcessingPending;
    private bool _isReprocessRequested;

    public ReviewEventProcessingCoordinator(
        ReviewEventCollectionCoordinator collectionCoordinator,
        ReviewEventCleanupCoordinator cleanupCoordinator,
        PendingReviewStartQueue pendingQueue,
        LoggingService loggingService,
        Func<ReviewEvent, Task<ReviewStartResult>> tryStartAutomaticallyAsync)
    {
        _collectionCoordinator = collectionCoordinator ?? throw new ArgumentNullException(nameof(collectionCoordinator));
        _cleanupCoordinator = cleanupCoordinator ?? throw new ArgumentNullException(nameof(cleanupCoordinator));
        _pendingQueue = pendingQueue ?? throw new ArgumentNullException(nameof(pendingQueue));
        _loggingService = loggingService ?? throw new ArgumentNullException(nameof(loggingService));
        _tryStartAutomaticallyAsync = tryStartAutomaticallyAsync ?? throw new ArgumentNullException(nameof(tryStartAutomaticallyAsync));
    }

    /// <summary>
    /// イベントを一覧へ追加し、終了済み PR を除外したうえで自動起動を試みる.
    /// </summary>
    /// <param name="reviewEvent">受信したレビューイベント.</param>
    /// <returns>自動起動結果。終了済み PR の場合は <see langword="null"/>.</returns>
    public async Task<ReviewEventProcessingResult> ProcessAsync(ReviewEvent reviewEvent)
    {
        ArgumentNullException.ThrowIfNull(reviewEvent);

        ReviewEvent? evictedEvent = _collectionCoordinator.Add(reviewEvent);
        _cleanupCoordinator.Track(reviewEvent);
        if (evictedEvent != null)
        {
            _cleanupCoordinator.Untrack(evictedEvent.EventId);
        }

        if (!await _cleanupCoordinator.IsActionAllowedAsync(reviewEvent, CancellationToken.None))
        {
            return new ReviewEventProcessingResult(null);
        }

        ReviewStartResult startResult = await _tryStartAutomaticallyAsync(reviewEvent);
        return new ReviewEventProcessingResult(startResult);
    }

    /// <summary>
    /// 実行中のため保留したイベントを、保留した順に再評価し、最初に起動できた 1 件を返す（#339）.
    /// </summary>
    /// <remarks>
    /// 一覧から消えたイベント（手動削除・保持上限による押し出し・終了済み PR の自動削除）と、
    /// 終了済みと判明した PR は起動せずに保留から外す。再評価でも通常の自動起動と同じ判定を通すため、
    /// 設定 off・Auto-Pause で見送ったイベントも保留から外れる。再び実行中と判定された場合は、
    /// 保留を残したまま次の実行終了を待つ。
    /// 再評価の await 中に呼ばれた場合は重ねて評価せず（同じイベントを二重に起動し得るため）、
    /// 進行中の再評価が終わった後に評価し直す.
    /// </remarks>
    /// <returns>起動したイベントと結果。起動しなかった場合は <see langword="null"/>.</returns>
    public async Task<PendingReviewStartResult?> ProcessPendingAsync()
    {
        if (_isProcessingPending)
        {
            _isReprocessRequested = true;
            return null;
        }

        _isProcessingPending = true;
        try
        {
            PendingReviewStartResult? started;
            do
            {
                _isReprocessRequested = false;
                started = await StartFirstPendingAsync();
            }
            while (started is null && _isReprocessRequested);

            return started;
        }
        finally
        {
            _isProcessingPending = false;
        }
    }

    private async Task<PendingReviewStartResult?> StartFirstPendingAsync()
    {
        while (_pendingQueue.Peek() is ReviewEvent pendingEvent)
        {
            if (!_collectionCoordinator.Events.Contains(pendingEvent))
            {
                _pendingQueue.Remove(pendingEvent);
                await _loggingService.WriteAsync(
                    $"[Auto] 保留していた {pendingEvent.PrCaption} は一覧から削除されたため、自動起動しません。");
                continue;
            }

            if (!await _cleanupCoordinator.IsActionAllowedAsync(pendingEvent, CancellationToken.None))
            {
                _pendingQueue.Remove(pendingEvent);
                continue;
            }

            await _loggingService.WriteAsync(
                $"[Auto] 保留していた {pendingEvent.PrCaption} の自動起動を再評価します（reason: {pendingEvent.Reason}）。");
            ReviewStartResult startResult = await _tryStartAutomaticallyAsync(pendingEvent);
            if (startResult.Status == ReviewStartStatus.SkippedBusy)
            {
                return null;
            }

            _pendingQueue.Remove(pendingEvent);
            if (startResult.IsStarted)
            {
                return new PendingReviewStartResult(pendingEvent, startResult);
            }
        }

        return null;
    }
}

/// <summary>
/// レビューイベント処理後に UI が反映する自動起動結果.
/// </summary>
internal sealed record ReviewEventProcessingResult(ReviewStartResult? StartResult)
{
    public bool ShouldNotify => StartResult is not null;
}

/// <summary>
/// 保留から自動起動したイベントと、その起動結果（#339）.
/// </summary>
internal sealed record PendingReviewStartResult(ReviewEvent ReviewEvent, ReviewStartResult StartResult);
