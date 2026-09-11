// <copyright file="ReviewEventCleanupCoordinator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

internal sealed class ReviewEventsRemovedEventArgs : EventArgs
{
    public ReviewEventsRemovedEventArgs(IReadOnlyList<string> eventIds)
    {
        EventIds = eventIds;
    }

    public IReadOnlyList<string> EventIds { get; }
}

/// <summary>
/// Recent review events の PR 状態を確認し、マージ済み・クローズ済みのイベントを片付ける.
/// </summary>
internal sealed class ReviewEventCleanupCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan _defaultPollInterval = TimeSpan.FromMinutes(5);
    private readonly IPullRequestStatusClient _statusClient;
    private readonly LoggingService _loggingService;
    private readonly TimeSpan _pollInterval;
    private readonly Dictionary<string, ReviewEvent> _trackedEvents = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _pollTask;

    public ReviewEventCleanupCoordinator(
        IPullRequestStatusClient statusClient,
        LoggingService loggingService,
        TimeSpan? pollInterval = null)
    {
        _statusClient = statusClient ?? throw new ArgumentNullException(nameof(statusClient));
        _loggingService = loggingService ?? throw new ArgumentNullException(nameof(loggingService));
        _pollInterval = pollInterval ?? _defaultPollInterval;
        if (_pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), "PR 状態の巡回間隔は正の値である必要があります。");
        }
    }

    public event EventHandler<ReviewEventsRemovedEventArgs>? EventsRemoved;

    internal int TrackedEventCount
    {
        get
        {
            lock (_lock)
            {
                return _trackedEvents.Count;
            }
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            _pollTask ??= PollAsync(_cts.Token);
        }
    }

    public void Track(ReviewEvent reviewEvent)
    {
        ArgumentNullException.ThrowIfNull(reviewEvent);
        reviewEvent.Validate();

        lock (_lock)
        {
            _trackedEvents[reviewEvent.EventId] = reviewEvent;
        }
    }

    public void Untrack(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return;
        }

        lock (_lock)
        {
            _trackedEvents.Remove(eventId);
        }
    }

    /// <summary>
    /// 起動直前の安全弁。状態を確認できない場合はイベントを残して操作を許可する.
    /// </summary>
    /// <param name="reviewEvent">起動対象のレビューイベント.</param>
    /// <param name="cancellationToken">状態照会をキャンセルするトークン.</param>
    /// <returns>PR がオープン中、または状態を確認できず安全側へフォールバックした場合は <see langword="true"/>.</returns>
    public async Task<bool> IsActionAllowedAsync(ReviewEvent reviewEvent, CancellationToken cancellationToken)
    {
        Track(reviewEvent);

        PullRequestLifecycleState state;
        try
        {
            state = await _statusClient.GetStateAsync(
                reviewEvent.Repository,
                reviewEvent.PrNumber,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await LogLookupFailureAsync(reviewEvent, ex).ConfigureAwait(false);
            return true;
        }

        if (IsClosed(state))
        {
            RemoveEvent(reviewEvent.EventId);
            await _loggingService.WriteAsync(
                $"レビューイベントを自動削除しました（PR が {GetStateLabel(state)} のため）: {reviewEvent.Repository}#{reviewEvent.PrNumber}").ConfigureAwait(false);
            return false;
        }

        return true;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!await _refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            ReviewEvent[] snapshot = SnapshotEvents();
            foreach (ReviewEvent reviewEvent in snapshot)
            {
                PullRequestLifecycleState state;
                try
                {
                    state = await _statusClient.GetStateAsync(
                        reviewEvent.Repository,
                        reviewEvent.PrNumber,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    await LogLookupFailureAsync(reviewEvent, ex).ConfigureAwait(false);
                    continue;
                }

                if (!IsClosed(state) || !RemoveEvent(reviewEvent.EventId))
                {
                    continue;
                }

                await _loggingService.WriteAsync(
                    $"レビューイベントを自動削除しました（PR が {GetStateLabel(state)} のため）: {reviewEvent.Repository}#{reviewEvent.PrNumber}").ConfigureAwait(false);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        Task? pollTask;
        lock (_lock)
        {
            pollTask = _pollTask;
        }

        if (pollTask != null)
        {
            try
            {
                await pollTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常終了
            }
        }

        // ウィンドウ表示時に開始した RefreshAsync が完了してから、共有ゲートを破棄する。
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        _refreshGate.Release();
        _refreshGate.Dispose();
        _cts.Dispose();
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_pollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 終了処理によるキャンセル
        }
    }

    private ReviewEvent[] SnapshotEvents()
    {
        lock (_lock)
        {
            return _trackedEvents.Values.ToArray();
        }
    }

    private bool RemoveEvent(string eventId)
    {
        bool removed;
        lock (_lock)
        {
            removed = _trackedEvents.Remove(eventId);
        }

        if (removed)
        {
            try
            {
                EventsRemoved?.Invoke(this, new ReviewEventsRemovedEventArgs(new[] { eventId }));
            }
            catch (Exception ex)
            {
                _ = _loggingService.WriteAsync($"レビューイベント一覧の自動削除通知に失敗しました: {ex.Message}");
            }
        }

        return removed;
    }

    private async Task LogLookupFailureAsync(ReviewEvent reviewEvent, Exception exception)
    {
        await _loggingService.WriteAsync(
            $"PR 状態の取得に失敗しました。イベントは保持します: {reviewEvent.Repository}#{reviewEvent.PrNumber}: {exception.Message}").ConfigureAwait(false);
    }

    private static bool IsClosed(PullRequestLifecycleState state)
        => state is PullRequestLifecycleState.Closed or PullRequestLifecycleState.Merged;

    private static string GetStateLabel(PullRequestLifecycleState state)
        => state switch
        {
            PullRequestLifecycleState.Merged => "マージ済み",
            PullRequestLifecycleState.Closed => "クローズ済み",
            _ => "終了済み",
        };
}
