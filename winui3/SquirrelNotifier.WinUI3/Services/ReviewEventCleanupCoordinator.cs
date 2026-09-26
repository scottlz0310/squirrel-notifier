// <copyright file="ReviewEventCleanupCoordinator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Globalization;
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

internal sealed class PullRequestClosedEventArgs : EventArgs
{
    public PullRequestClosedEventArgs(string repository, int prNumber)
    {
        Repository = repository;
        PrNumber = prNumber;
    }

    public string Repository { get; }

    public int PrNumber { get; }
}

/// <summary>
/// Recent review events の PR 状態を確認し、マージ済み・クローズ済みのイベントを片付ける.
/// 照会は PR 単位にまとめ、未認証 GitHub API のレート制限を消費し尽くさない予算内で行う.
/// </summary>
internal sealed class ReviewEventCleanupCoordinator : IAsyncDisposable
{
    // 未認証 GitHub API（60 req/h / IP）の半分を上限にし、残りを更新チェック等に残す。
    // 巡回の照会数は任意の 1 時間で容量 5 + 補充 25 = 30 回を超えない。
    private const int _requestBurstCapacity = 5;
    private const int _requestRefillPerHour = 25;
    private static readonly TimeSpan _defaultPollInterval = TimeSpan.FromMinutes(5);
    private readonly IPullRequestStatusClient _statusClient;
    private readonly LoggingService _loggingService;
    private readonly TimeSpan _pollInterval;
    private readonly TimeProvider _timeProvider;
    private readonly RequestTokenBucket _requestBudget;
    private readonly Dictionary<string, ReviewEvent> _trackedEvents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastCheckedAt = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _pollTask;
    private DateTimeOffset? _rateLimitedUntil;

    public ReviewEventCleanupCoordinator(
        IPullRequestStatusClient statusClient,
        LoggingService loggingService,
        TimeSpan? pollInterval = null,
        TimeProvider? timeProvider = null)
    {
        _statusClient = statusClient ?? throw new ArgumentNullException(nameof(statusClient));
        _loggingService = loggingService ?? throw new ArgumentNullException(nameof(loggingService));
        _pollInterval = pollInterval ?? _defaultPollInterval;
        if (_pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), "PR 状態の巡回間隔は正の値である必要があります。");
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
        _requestBudget = new RequestTokenBucket(_requestBurstCapacity, _requestRefillPerHour, _timeProvider);
    }

    public event EventHandler<ReviewEventsRemovedEventArgs>? EventsRemoved;

    /// <summary>
    /// 追跡中の PR がマージ済み・クローズ済みと判明したときに発火する（reviewer の作業領域の片付け、#403）.
    /// </summary>
    public event EventHandler<PullRequestClosedEventArgs>? PullRequestClosed;

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
    /// 起動直前の安全弁。状態を確認できない場合やレート制限中はイベントを残して操作を許可する.
    /// ユーザー操作を起点とするため、巡回の予算が不足していても照会する.
    /// </summary>
    /// <param name="reviewEvent">起動対象のレビューイベント.</param>
    /// <param name="cancellationToken">状態照会をキャンセルするトークン.</param>
    /// <returns>PR がオープン中、または状態を確認できず安全側へフォールバックした場合は <see langword="true"/>.</returns>
    public async Task<bool> IsActionAllowedAsync(ReviewEvent reviewEvent, CancellationToken cancellationToken)
    {
        Track(reviewEvent);

        if (GetRateLimitedUntil() is DateTimeOffset rateLimitedUntil)
        {
            await _loggingService.WriteAsync(
                $"GitHub API のレート制限中（{FormatLocalTime(rateLimitedUntil)} まで）のため、PR 状態を確認せずに操作を許可します: {reviewEvent.Repository}#{reviewEvent.PrNumber}").ConfigureAwait(false);
            return true;
        }

        string pullRequestKey = GetPullRequestKey(reviewEvent.Repository, reviewEvent.PrNumber);
        _requestBudget.AcquireWithDebt();
        MarkChecked(pullRequestKey);

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
        catch (GitHubRateLimitException ex)
        {
            await PauseForRateLimitAsync(ex).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            await LogLookupFailureAsync(reviewEvent.Repository, reviewEvent.PrNumber, ex).ConfigureAwait(false);
            return true;
        }

        if (IsClosed(state))
        {
            RemovePullRequestEvents(pullRequestKey);
            await LogRemovalAsync(reviewEvent.Repository, reviewEvent.PrNumber, state).ConfigureAwait(false);
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
            if (GetRateLimitedUntil() is not null)
            {
                return;
            }

            foreach (PullRequestTarget target in SnapshotPullRequestsByLastChecked())
            {
                if (!_requestBudget.TryAcquire())
                {
                    return;
                }

                MarkChecked(target.Key);
                PullRequestLifecycleState state;
                try
                {
                    state = await _statusClient.GetStateAsync(
                        target.Repository,
                        target.PrNumber,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (GitHubRateLimitException ex)
                {
                    await PauseForRateLimitAsync(ex).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex)
                {
                    await LogLookupFailureAsync(target.Repository, target.PrNumber, ex).ConfigureAwait(false);
                    continue;
                }

                if (IsClosed(state))
                {
                    if (RemovePullRequestEvents(target.Key))
                    {
                        await LogRemovalAsync(target.Repository, target.PrNumber, state).ConfigureAwait(false);
                    }

                    await NotifyPullRequestClosedAsync(target.Repository, target.PrNumber).ConfigureAwait(false);
                }
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
        using var timer = new PeriodicTimer(_pollInterval, _timeProvider);
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

    // 同じ PR の複数イベントを 1 回の照会にまとめ、最後に照会した時刻が古い PR から並べる。
    private PullRequestTarget[] SnapshotPullRequestsByLastChecked()
    {
        lock (_lock)
        {
            PullRequestTarget[] targets = _trackedEvents.Values
                .GroupBy(reviewEvent => GetPullRequestKey(reviewEvent.Repository, reviewEvent.PrNumber), StringComparer.Ordinal)
                .Select(group => new PullRequestTarget(group.Key, group.First().Repository, group.First().PrNumber))
                .ToArray();

            foreach (string staleKey in _lastCheckedAt.Keys.Except(targets.Select(target => target.Key), StringComparer.Ordinal).ToArray())
            {
                _lastCheckedAt.Remove(staleKey);
            }

            return targets
                .OrderBy(target => _lastCheckedAt.TryGetValue(target.Key, out DateTimeOffset checkedAt) ? checkedAt : DateTimeOffset.MinValue)
                .ToArray();
        }
    }

    private void MarkChecked(string pullRequestKey)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            _lastCheckedAt[pullRequestKey] = now;
        }
    }

    private DateTimeOffset? GetRateLimitedUntil()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            if (_rateLimitedUntil is DateTimeOffset until && now >= until)
            {
                _rateLimitedUntil = null;
            }

            return _rateLimitedUntil;
        }
    }

    private async Task PauseForRateLimitAsync(GitHubRateLimitException exception)
    {
        DateTimeOffset resetAt;
        lock (_lock)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            DateTimeOffset current = _rateLimitedUntil is DateTimeOffset until && until > now
                ? until
                : DateTimeOffset.MinValue;
            resetAt = exception.ResetAt > current ? exception.ResetAt : current;
            _rateLimitedUntil = resetAt;
        }

        await _loggingService.WriteAsync(
            $"GitHub API のレート制限に達したため、{FormatLocalTime(resetAt)} まで PR 状態の確認を停止します。イベントは保持します: {exception.Message}").ConfigureAwait(false);
    }

    private async Task NotifyPullRequestClosedAsync(string repository, int prNumber)
    {
        try
        {
            PullRequestClosed?.Invoke(this, new PullRequestClosedEventArgs(repository, prNumber));
        }
        catch (Exception ex)
        {
            // 購読側の失敗で巡回を止めない。失敗は購読側の責務で記録されるが、未処理の例外もここで残す.
            await _loggingService.WriteAsync($"PR 完了の通知処理に失敗しました ({repository}#{prNumber}): {ex.Message}").ConfigureAwait(false);
        }
    }

    private bool RemovePullRequestEvents(string pullRequestKey)
    {
        string[] removedIds;
        lock (_lock)
        {
            removedIds = _trackedEvents.Values
                .Where(reviewEvent => GetPullRequestKey(reviewEvent.Repository, reviewEvent.PrNumber) == pullRequestKey)
                .Select(reviewEvent => reviewEvent.EventId)
                .ToArray();
            foreach (string eventId in removedIds)
            {
                _trackedEvents.Remove(eventId);
            }

            _lastCheckedAt.Remove(pullRequestKey);
        }

        if (removedIds.Length == 0)
        {
            return false;
        }

        try
        {
            EventsRemoved?.Invoke(this, new ReviewEventsRemovedEventArgs(removedIds));
        }
        catch (Exception ex)
        {
            _ = _loggingService.WriteAsync($"レビューイベント一覧の自動削除通知に失敗しました: {ex.Message}");
        }

        return true;
    }

    private async Task LogRemovalAsync(string repository, int prNumber, PullRequestLifecycleState state)
    {
        await _loggingService.WriteAsync(
            $"レビューイベントを自動削除しました（PR が {GetStateLabel(state)} のため）: {repository}#{prNumber}").ConfigureAwait(false);
    }

    private async Task LogLookupFailureAsync(string repository, int prNumber, Exception exception)
    {
        await _loggingService.WriteAsync(
            $"PR 状態の取得に失敗しました。イベントは保持します: {repository}#{prNumber}: {exception.Message}").ConfigureAwait(false);
    }

    // GitHub のリポジトリ名は大文字小文字を区別しないため、表記揺れのイベントも同じ PR として扱う。
    private static string GetPullRequestKey(string repository, int prNumber)
        => $"{repository.ToUpperInvariant()}#{prNumber.ToString(CultureInfo.InvariantCulture)}";

    private static string FormatLocalTime(DateTimeOffset value)
        => value.ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.CurrentCulture);

    private static bool IsClosed(PullRequestLifecycleState state)
        => state is PullRequestLifecycleState.Closed or PullRequestLifecycleState.Merged;

    private static string GetStateLabel(PullRequestLifecycleState state)
        => state switch
        {
            PullRequestLifecycleState.Merged => "マージ済み",
            PullRequestLifecycleState.Closed => "クローズ済み",
            _ => "終了済み",
        };

    private readonly record struct PullRequestTarget(string Key, string Repository, int PrNumber);
}
