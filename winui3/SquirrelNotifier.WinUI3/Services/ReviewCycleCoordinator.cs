// <copyright file="ReviewCycleCoordinator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

internal sealed class ReviewCycleStateChangedEventArgs : EventArgs
{
    public ReviewCycleStateChangedEventArgs(ReviewEvent reviewEvent, ReviewCycleState state)
    {
        ReviewEvent = reviewEvent ?? throw new ArgumentNullException(nameof(reviewEvent));
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    public ReviewEvent ReviewEvent { get; }

    public ReviewCycleState State { get; }
}

/// <summary>
/// PR 単位の reviewer サイクルを追跡する。GitHub や thread-owl の業務状態は保持せず、
/// queue event とローカル reviewer プロセスの観測結果だけを表示用に管理する.
/// </summary>
internal sealed class ReviewCycleCoordinator
{
    private const int _maxProcessedEventIds = 32;

    private readonly IReviewCycleStore _store;
    private readonly LoggingService _loggingService;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly Dictionary<string, ReviewCycleState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReviewEvent> _latestEvents = new(StringComparer.Ordinal);

    public ReviewCycleCoordinator(
        IReviewCycleStore store,
        LoggingService loggingService,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _loggingService = loggingService ?? throw new ArgumentNullException(nameof(loggingService));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler<ReviewCycleStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// reviewer 用イベントをサイクルへ取り込む。同じ event ID は再処理せず、
    /// re-review-requested のときだけラウンドを 1 つ進める.
    /// </summary>
    /// <param name="reviewEvent">受信したレビューイベント.</param>
    /// <returns>状態更新が完了したタスク.</returns>
    public async Task ObserveEventAsync(ReviewEvent reviewEvent)
    {
        ArgumentNullException.ThrowIfNull(reviewEvent);
        if (!ReviewNotificationPolicy.ShouldOfferReviewerAction(reviewEvent.Reason))
        {
            return;
        }

        ReviewCycleState? stateToPublish = null;
        bool isNewEvent = false;
        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            string key = CreateKey(reviewEvent);
            ReviewCycleState? existing = await LoadStateAsync(key, reviewEvent).ConfigureAwait(false);
            bool isDuplicate = existing is not null
                && existing.ProcessedEventIds.Contains(reviewEvent.EventId, StringComparer.Ordinal);
            if (isDuplicate)
            {
                stateToPublish = existing;
            }
            else
            {
                int round = existing?.Round ?? 1;
                if (existing is not null && reviewEvent.Reason == "re-review-requested")
                {
                    round++;
                }

                List<string> processedEventIds = existing?.ProcessedEventIds.ToList() ?? [];
                processedEventIds.Add(reviewEvent.EventId);
                if (processedEventIds.Count > _maxProcessedEventIds)
                {
                    processedEventIds = processedEventIds[^_maxProcessedEventIds..];
                }

                bool reviewerRunning = existing?.Status == ReviewCycleStatus.ReviewerRunning
                    && !string.IsNullOrWhiteSpace(existing.ActiveEventId);

                stateToPublish = new ReviewCycleState(
                    reviewEvent.Repository,
                    reviewEvent.PrNumber,
                    round,
                    reviewEvent.EventId,
                    reviewEvent.Reason,
                    reviewerRunning ? ReviewCycleStatus.ReviewerRunning : ReviewCycleStatus.AwaitingReviewer,
                    reviewerRunning ? existing!.ActiveEventId : null,
                    reviewerRunning ? existing!.ActiveRound ?? existing.Round : null,
                    _timeProvider.GetUtcNow(),
                    processedEventIds);
                await SaveStateAsync(key, stateToPublish).ConfigureAwait(false);
                isNewEvent = true;
            }

            if (!isDuplicate)
            {
                _latestEvents[key] = reviewEvent;
            }
        }
        finally
        {
            _stateGate.Release();
        }

        if (stateToPublish is not null)
        {
            PublishStateChanged(reviewEvent, stateToPublish);
        }

        if (isNewEvent)
        {
            await _loggingService.WriteAsync(
                $"[Cycle] {reviewEvent.PrCaption} のレビューサイクルをラウンド {stateToPublish!.Round} として記録しました（reason: {reviewEvent.Reason}）。").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// reviewer の起動をサイクルへ記録し、プロセス終了時にローカル観測状態を更新する.
    /// </summary>
    /// <param name="reviewEvent">起動対象のレビューイベント.</param>
    /// <param name="launch">起動した reviewer セッション.</param>
    /// <returns>起動状態の記録が完了したタスク.</returns>
    public async Task MarkReviewerStartedAsync(ReviewEvent reviewEvent, ReviewStartLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(reviewEvent);
        ArgumentNullException.ThrowIfNull(launch);

        ReviewCycleState state;
        string key = CreateKey(reviewEvent);
        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ReviewCycleState? existing = await LoadStateAsync(key, reviewEvent).ConfigureAwait(false);
            state = existing ?? CreateInitialState(reviewEvent);
            state = state with
            {
                Status = ReviewCycleStatus.ReviewerRunning,
                ActiveEventId = reviewEvent.EventId,
                ActiveRound = state.Round,
                UpdatedAt = _timeProvider.GetUtcNow(),
            };
            await SaveStateAsync(key, state).ConfigureAwait(false);

            if (existing is null || string.Equals(existing.LastEventId, reviewEvent.EventId, StringComparison.Ordinal))
            {
                _latestEvents[key] = reviewEvent;
            }
        }
        finally
        {
            _stateGate.Release();
        }

        PublishStateChanged(reviewEvent, state);
        await _loggingService.WriteAsync(
            $"[Cycle] {reviewEvent.PrCaption} ラウンド {state.Round} の reviewer を起動しました。").ConfigureAwait(false);
        _ = ObserveCompletionAsync(reviewEvent, state.Round, reviewEvent.EventId, launch.Session);
    }

    private async Task ObserveCompletionAsync(
        ReviewEvent reviewEvent,
        int round,
        string eventId,
        AgentExecutionSession session)
    {
        try
        {
            LauncherResult result = await session.Completion.ConfigureAwait(false);
            ReviewCycleState? stateToPublish = null;
            ReviewCycleState? latestStateToPublish = null;
            ReviewEvent? latestEventToPublish = null;
            string key = CreateKey(reviewEvent);

            await _stateGate.WaitAsync().ConfigureAwait(false);
            try
            {
                ReviewCycleState? current = await LoadStateAsync(key, reviewEvent).ConfigureAwait(false);
                if (current is null
                    || !string.Equals(current.ActiveEventId, eventId, StringComparison.Ordinal))
                {
                    return;
                }

                ReviewCycleStatus completionStatus = result.Success
                    ? ReviewCycleStatus.ReviewerCompleted
                    : ReviewCycleStatus.ReviewerFailed;
                int activeRound = current.ActiveRound ?? round;
                bool newerEventPending = !string.Equals(current.LastEventId, eventId, StringComparison.Ordinal);
                ReviewCycleState completedState = current with
                {
                    Round = activeRound,
                    Status = completionStatus,
                    ActiveEventId = null,
                    ActiveRound = null,
                    UpdatedAt = _timeProvider.GetUtcNow(),
                };
                stateToPublish = completedState;

                ReviewCycleState persistedState = newerEventPending
                    ? current with
                    {
                        Status = ReviewCycleStatus.AwaitingReviewer,
                        ActiveEventId = null,
                        ActiveRound = null,
                        UpdatedAt = _timeProvider.GetUtcNow(),
                    }
                    : completedState;
                await SaveStateAsync(key, persistedState).ConfigureAwait(false);

                if (newerEventPending
                    && _latestEvents.TryGetValue(key, out ReviewEvent? latestEvent)
                    && !string.Equals(latestEvent.EventId, eventId, StringComparison.Ordinal))
                {
                    latestEventToPublish = latestEvent;
                    latestStateToPublish = persistedState;
                }
            }
            finally
            {
                _stateGate.Release();
            }

            if (stateToPublish is null)
            {
                return;
            }

            PublishStateChanged(reviewEvent, stateToPublish);
            if (latestEventToPublish is not null && latestStateToPublish is not null)
            {
                PublishStateChanged(latestEventToPublish, latestStateToPublish);
            }

            string status = result.Success ? "終了しました（結果未確認）" : "失敗しました";
            await _loggingService.WriteAsync(
                $"[Cycle] {reviewEvent.PrCaption} ラウンド {round} の reviewer 実行が{status}。").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _loggingService.WriteAsync(
                $"[Cycle] {reviewEvent.PrCaption} ラウンド {round} の終了状態を記録できませんでした: {ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task<ReviewCycleState?> LoadStateAsync(string key, ReviewEvent reviewEvent)
    {
        if (_states.TryGetValue(key, out ReviewCycleState? state))
        {
            DateTimeOffset cutoff = _timeProvider.GetUtcNow() - ReviewCycleStore.DefaultTtl;
            if (state.UpdatedAt > cutoff)
            {
                return state;
            }

            _states.Remove(key);
        }

        try
        {
            state = await _store.TryGetAsync(
                reviewEvent.Repository,
                reviewEvent.PrNumber).ConfigureAwait(false);
            if (state is not null)
            {
                _states[key] = state;
            }

            return state;
        }
        catch (Exception ex)
        {
            await _loggingService.WriteAsync(
                $"[Cycle] {reviewEvent.PrCaption} の状態を読み込めません。今回の実行中はメモリ上で追跡します: {ex.Message}").ConfigureAwait(false);
            return null;
        }
    }

    private async Task SaveStateAsync(string key, ReviewCycleState state)
    {
        _states[key] = state;
        try
        {
            await _store.SaveAsync(state).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _loggingService.WriteAsync(
                $"[Cycle] {state.Repository}#{state.PrNumber} の状態を保存できません。今回の実行中はメモリ上で追跡します: {ex.Message}").ConfigureAwait(false);
        }
    }

    private void PublishStateChanged(ReviewEvent reviewEvent, ReviewCycleState state)
    {
        try
        {
            StateChanged?.Invoke(this, new ReviewCycleStateChangedEventArgs(reviewEvent, state));
        }
        catch (Exception ex)
        {
            _ = _loggingService.WriteAsync(
                $"[Cycle] {reviewEvent.PrCaption} の表示状態更新通知に失敗しました: {ex.Message}");
        }
    }

    private ReviewCycleState CreateInitialState(ReviewEvent reviewEvent)
        => new(
            reviewEvent.Repository,
            reviewEvent.PrNumber,
            1,
            reviewEvent.EventId,
            reviewEvent.Reason,
            ReviewCycleStatus.AwaitingReviewer,
            null,
            null,
            _timeProvider.GetUtcNow(),
            [reviewEvent.EventId]);

    private static string CreateKey(ReviewEvent reviewEvent)
        => $"{reviewEvent.Repository.Trim().ToUpperInvariant()}#{reviewEvent.PrNumber}";
}
