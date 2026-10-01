// <copyright file="ReviewStatusService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text.Json;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// reviewer の起動待ち・実行中・終了の状態と、時刻・保留の理由を、<c>review-status.json</c> へ
/// アトミックに書き出す公開契約（#462。スキーマと使い方は <c>docs/review-status-contract.md</c>）.
/// </summary>
/// <remarks>
/// <para>
/// 内部ストアの <c>review-cycles.json</c> と、statusline 向けの <c>statusline-summary.json</c>（#427）とは独立している。
/// 内容はこのプロセスが観測した状態だけで構成する実行状態の写しで、レビューの結果の正本
/// （thread-owl の <c>review://status</c>、GitHub）にはしない。起動時は空の文書を書き出し、終了時は削除する
/// （ファイル不在 = 未起動）。<c>updatedAt</c> は状態が変わらなくても一定間隔で更新する（異常終了で残った古い
/// ファイルを、消費者が判定できるようにするため）.
/// </para>
/// </remarks>
internal sealed class ReviewStatusService
{
    internal const string FileName = "review-status.json";

    /// <summary>状態が変わらなくても <c>updatedAt</c> を更新する間隔.</summary>
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);

    private const int _maxReplaceAttempts = 3;
    private static readonly TimeSpan _replaceRetryDelay = TimeSpan.FromMilliseconds(50);
    private static readonly JsonSerializerOptions _serializerOptions = new();

    private readonly string _statusPath;
    private readonly string _tempPath;
    private readonly ReviewCycleCoordinator _reviewCycleCoordinator;
    private readonly ReviewEventCleanupCoordinator _cleanupCoordinator;
    private readonly LoggingService _loggingService;
    private readonly TimeProvider _timeProvider;
    private readonly string _appVersion;
    private readonly DateTimeOffset _startedAt;
    private readonly Func<bool> _isAutoStartEnabled;
    private readonly Func<SubscriptionState> _getSubscriptionState;
    private readonly Func<bool> _isAuthenticationRequired;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    // 起動待ち・実行中（PR につき 1 件）と、終了（新しい順に上限まで）
    private readonly Dictionary<string, ReviewStatusEntry> _current = new(StringComparer.Ordinal);
    private readonly List<ReviewStatusEntry> _finished = [];
    private readonly Dictionary<string, DateTimeOffset> _lastAppliedAt = new(StringComparer.Ordinal);

    // PR に紐づく event ID（最新・実行中・実行した event）。実行中に届いた次の event だけが削除された場合も、
    // その PR の状態を落とせるようにする
    private readonly Dictionary<string, HashSet<string>> _relatedEventIds = new(StringComparer.Ordinal);

    // 保留した event ごとの理由。同じ PR の実行中に届いた次の event は、実行が終わって起動待ちになる時点で、
    // 実行中に観測した保留の理由を引き継ぐ。保留を観測する時点では、その event の起動待ちの項目がまだ無いことがある
    private readonly Dictionary<string, HoldObservation> _holds = new(StringComparer.Ordinal);

    // 終了済み PR のイベントを削除した後にも、実行中だった reviewer の終了通知が同じイベントの状態を
    // 発行する。削除済みイベントの状態で PR を再登録しないよう、削除した ID を保持する
    private readonly HashSet<string> _removedEventIds = new(StringComparer.Ordinal);
    private string _subscriptionState;
    private DateTimeOffset _subscriptionSince;
    private ITimer? _heartbeat;
    private bool _isShutDown;

    public ReviewStatusService(
        string outputDirectory,
        ReviewCycleCoordinator reviewCycleCoordinator,
        ReviewEventCleanupCoordinator cleanupCoordinator,
        LoggingService loggingService,
        string appVersion,
        Func<bool> isAutoStartEnabled,
        Func<SubscriptionState> getSubscriptionState,
        Func<bool> isAuthenticationRequired,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("review-status の出力先ディレクトリが不正です。", nameof(outputDirectory));
        }

        _reviewCycleCoordinator = reviewCycleCoordinator ?? throw new ArgumentNullException(nameof(reviewCycleCoordinator));
        _cleanupCoordinator = cleanupCoordinator ?? throw new ArgumentNullException(nameof(cleanupCoordinator));
        _loggingService = loggingService ?? throw new ArgumentNullException(nameof(loggingService));
        _appVersion = appVersion ?? throw new ArgumentNullException(nameof(appVersion));
        _isAutoStartEnabled = isAutoStartEnabled ?? throw new ArgumentNullException(nameof(isAutoStartEnabled));
        _getSubscriptionState = getSubscriptionState ?? throw new ArgumentNullException(nameof(getSubscriptionState));
        _isAuthenticationRequired = isAuthenticationRequired ?? throw new ArgumentNullException(nameof(isAuthenticationRequired));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startedAt = _timeProvider.GetUtcNow();
        _subscriptionSince = _startedAt;
        _subscriptionState = DescribeSubscription();

        Directory.CreateDirectory(outputDirectory);
        _statusPath = Path.Combine(outputDirectory, FileName);
        _tempPath = _statusPath + ".tmp";

        _reviewCycleCoordinator.StateChanged += OnReviewCycleStateChanged;
        _reviewCycleCoordinator.HoldObserved += OnHoldObserved;
        _cleanupCoordinator.EventsRemoved += OnReviewEventsRemoved;
    }

    /// <summary>前回のプロセスが残した文書を空の文書で置き換え、鮮度の更新（heartbeat）を始める.</summary>
    /// <returns>書き出しが完了したタスク.</returns>
    public async Task StartAsync()
    {
        await UpdateAsync(() => true).ConfigureAwait(false);
        _heartbeat = _timeProvider.CreateTimer(
            static state => _ = ((ReviewStatusService)state!).RefreshAsync(),
            this,
            HeartbeatInterval,
            HeartbeatInterval);
    }

    /// <summary>購読を解除し、文書を削除する。以降の状態変化は書き出さない.</summary>
    /// <returns>削除が完了したタスク.</returns>
    public async Task ShutdownAsync()
    {
        _reviewCycleCoordinator.StateChanged -= OnReviewCycleStateChanged;
        _reviewCycleCoordinator.HoldObserved -= OnHoldObserved;
        _cleanupCoordinator.EventsRemoved -= OnReviewEventsRemoved;

        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _isShutDown = true;
            _heartbeat?.Dispose();
            _heartbeat = null;
            File.Delete(_tempPath);
            File.Delete(_statusPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _loggingService.WriteAsync(
                $"[ReviewStatus] 公開状態を削除できません: {_statusPath}: {ex.Message}").ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>購読の状態が変わったことを反映する。状態の文言が変わった場合だけ書き出す.</summary>
    /// <returns>反映が完了したタスク.</returns>
    public Task NotifySubscriptionChangedAsync()
        => UpdateAsync(() =>
        {
            string next = DescribeSubscription();
            if (string.Equals(next, _subscriptionState, StringComparison.Ordinal))
            {
                return false;
            }

            _subscriptionState = next;
            _subscriptionSince = _timeProvider.GetUtcNow();
            return true;
        });

    /// <summary>状態が変わっていなくても、<c>updatedAt</c> を現在時刻へ更新する（heartbeat）.</summary>
    /// <returns>書き出しが完了したタスク.</returns>
    internal Task RefreshAsync() => UpdateAsync(() => true);

    internal Task ApplyStateAsync(ReviewEvent reviewEvent, ReviewCycleState state, int? exitCode = null)
    {
        ArgumentNullException.ThrowIfNull(reviewEvent);
        ArgumentNullException.ThrowIfNull(state);

        return UpdateAsync(() => ApplyState(reviewEvent, state, exitCode));
    }

    internal Task ApplyHoldAsync(ReviewEvent reviewEvent, ReviewHoldKind kind)
    {
        ArgumentNullException.ThrowIfNull(reviewEvent);

        return UpdateAsync(() => ApplyHold(reviewEvent, kind));
    }

    internal Task RemoveEventsAsync(IReadOnlyCollection<string> eventIds)
    {
        ArgumentNullException.ThrowIfNull(eventIds);

        return UpdateAsync(() =>
        {
            _removedEventIds.UnionWith(eventIds);
            foreach (string eventId in eventIds)
            {
                _holds.Remove(eventId);
            }

            string[] keysToRemove = [.. _current
                .Where(pair => eventIds.Contains(pair.Value.EventId)
                    || (_relatedEventIds.TryGetValue(pair.Key, out HashSet<string>? related) && related.Overlaps(eventIds)))
                .Select(static pair => pair.Key)];
            foreach (string key in keysToRemove)
            {
                _current.Remove(key);
                _relatedEventIds.Remove(key);
            }

            int removedFinished = _finished.RemoveAll(entry => eventIds.Contains(entry.EventId));
            return keysToRemove.Length > 0 || removedFinished > 0;
        });
    }

    private static DateTimeOffset ToUtc(DateTime time) => new DateTimeOffset(time).ToUniversalTime();

    private readonly record struct HoldObservation(ReviewHoldKind Kind, DateTimeOffset Since);

    private bool ApplyState(ReviewEvent reviewEvent, ReviewCycleState state, int? exitCode)
    {
        if (state.Status == ReviewCycleStatus.Unknown)
        {
            return false;
        }

        // 起動待ちは最新の event、実行中は起動した event、終了は実行した event を指す
        string eventId = state.Status switch
        {
            ReviewCycleStatus.AwaitingReviewer => state.LastEventId,
            ReviewCycleStatus.ReviewerRunning => state.ActiveEventId ?? state.LastEventId,
            _ => reviewEvent.EventId,
        };
        string key = ReviewStatusDocumentBuilder.CreateKey(state.Repository, state.PrNumber);

        // 状態変化の通知はロックの外で発火されるため、到着順が前後した古い状態で上書きしない
        if (_removedEventIds.Contains(eventId)
            || (_lastAppliedAt.TryGetValue(key, out DateTimeOffset lastAppliedAt) && lastAppliedAt > state.UpdatedAt))
        {
            return false;
        }

        _lastAppliedAt[key] = state.UpdatedAt;
        TrackRelatedEvents(key, reviewEvent, state);
        _current.TryGetValue(key, out ReviewStatusEntry? existing);

        switch (state.Status)
        {
            case ReviewCycleStatus.AwaitingReviewer:
                // 同じ PR の保留中に新しい event が届いた場合、待ち順（保留し始めた時刻）は保つ
                ReviewStatusEntry? previousWait = existing is { State: ReviewStatusState.Waiting } ? existing : null;
                ReviewHoldKind? hold = previousWait?.Hold;
                DateTimeOffset? holdSince = previousWait?.HoldSince;
                if (_holds.TryGetValue(eventId, out HoldObservation observed))
                {
                    hold = observed.Kind;
                    holdSince = observed.Since;
                }

                if (previousWait is not null && previousWait.EventId != eventId)
                {
                    _holds.Remove(previousWait.EventId);
                }

                _current[key] = new ReviewStatusEntry(
                    key,
                    state.Repository,
                    state.PrNumber,
                    state.Round,
                    eventId,
                    state.LastReason,
                    ReviewStatusState.Waiting,
                    ToUtc(reviewEvent.ReceivedTime),
                    Hold: hold,
                    HoldSince: holdSince);
                return true;

            case ReviewCycleStatus.ReviewerRunning:
                // 実行中に同じ PR の次の event が届くと、実行中の状態（ActiveEventId）に、次の event の LastEventId・
                // LastReason・受信時刻を載せて通知される。実行中の event の受信・開始時刻と reason は、上書きしない
                if (existing is { State: ReviewStatusState.Running } && existing.EventId == eventId)
                {
                    return false;
                }

                ReviewStatusEntry? waited = existing is { State: ReviewStatusState.Waiting } && existing.EventId == eventId
                    ? existing
                    : null;
                bool isActiveEvent = reviewEvent.EventId == eventId;
                _holds.Remove(eventId);
                _current[key] = new ReviewStatusEntry(
                    key,
                    state.Repository,
                    state.PrNumber,
                    state.ActiveRound ?? state.Round,
                    eventId,
                    isActiveEvent ? reviewEvent.Reason : state.LastReason,
                    ReviewStatusState.Running,
                    waited?.ReceivedAt ?? (isActiveEvent ? ToUtc(reviewEvent.ReceivedTime) : state.UpdatedAt),
                    StartedAt: state.UpdatedAt,
                    Agent: state.ActiveAgent);
                return true;

            default:
                ReviewStatusEntry? running = existing is { State: ReviewStatusState.Running } && existing.EventId == eventId
                    ? existing
                    : null;
                if (running is not null)
                {
                    _current.Remove(key);
                }

                _holds.Remove(eventId);
                _finished.RemoveAll(entry => entry.EventId == eventId);
                _finished.Add(new ReviewStatusEntry(
                    key,
                    state.Repository,
                    state.PrNumber,
                    state.Round,
                    eventId,
                    running?.Reason ?? reviewEvent.Reason,
                    ReviewStatusState.Finished,
                    running?.ReceivedAt ?? ToUtc(reviewEvent.ReceivedTime),
                    StartedAt: running?.StartedAt,
                    FinishedAt: state.UpdatedAt,
                    Agent: running?.Agent,
                    Outcome: state.Status == ReviewCycleStatus.ReviewerCompleted ? ReviewerOutcome.Completed : ReviewerOutcome.Failed,
                    ExitCode: exitCode));
                return true;
        }
    }

    private void TrackRelatedEvents(string key, ReviewEvent reviewEvent, ReviewCycleState state)
    {
        if (!_relatedEventIds.TryGetValue(key, out HashSet<string>? related))
        {
            related = new HashSet<string>(StringComparer.Ordinal);
            _relatedEventIds[key] = related;
        }

        related.Add(reviewEvent.EventId);
        related.Add(state.LastEventId);
        if (state.ActiveEventId is not null)
        {
            related.Add(state.ActiveEventId);
        }
    }

    private bool ApplyHold(ReviewEvent reviewEvent, ReviewHoldKind kind)
    {
        if (_removedEventIds.Contains(reviewEvent.EventId))
        {
            return false;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        PruneHolds(now);

        // 保留し始めた時刻は、理由が変わっても保つ（待ち順は、保留し始めた順）
        DateTimeOffset since = _holds.TryGetValue(reviewEvent.EventId, out HoldObservation previous) ? previous.Since : now;
        _holds[reviewEvent.EventId] = new HoldObservation(kind, since);

        // 起動待ちの項目に反映するのは、その event の項目だけ。実行中の PR の次の event の保留は、実行が終わって
        // 起動待ちになる時点（ApplyState）で反映する。古い event の遅れた通知で、新しい event の項目を書き換えない
        string key = ReviewStatusDocumentBuilder.CreateKey(reviewEvent.Repository, reviewEvent.PrNumber);
        if (!_current.TryGetValue(key, out ReviewStatusEntry? entry)
            || entry.State != ReviewStatusState.Waiting
            || entry.EventId != reviewEvent.EventId
            || entry.Hold == kind)
        {
            return false;
        }

        _current[key] = entry with { Hold = kind, HoldSince = entry.HoldSince ?? since };
        return true;
    }

    // 起動待ちにも実行にもならなかった event の保留を、いつまでも持たない
    private void PruneHolds(DateTimeOffset now)
    {
        DateTimeOffset cutoff = now - ReviewStatusDocumentBuilder.RecentRetention;
        string[] expired = [.. _holds.Where(pair => pair.Value.Since < cutoff).Select(static pair => pair.Key)];
        foreach (string eventId in expired)
        {
            _holds.Remove(eventId);
        }
    }

    private string DescribeSubscription()
    {
        SubscriptionState state = _getSubscriptionState();
        if (state != SubscriptionState.Running && _isAuthenticationRequired())
        {
            return "authRequired";
        }

        return state switch
        {
            SubscriptionState.Running => "running",
            SubscriptionState.Starting => "starting",
            SubscriptionState.Stopping => "stopping",
            SubscriptionState.Stopped => "stopped",
            _ => "error",
        };
    }

    private void OnReviewCycleStateChanged(object? sender, ReviewCycleStateChangedEventArgs e)
        => _ = ApplyStateAsync(e.ReviewEvent, e.State, e.ExitCode);

    private void OnHoldObserved(object? sender, ReviewHoldObservedEventArgs e)
        => _ = ApplyHoldAsync(e.ReviewEvent, e.Kind);

    private void OnReviewEventsRemoved(object? sender, ReviewEventsRemovedEventArgs e)
        => _ = RemoveEventsAsync(e.EventIds);

    private async Task UpdateAsync(Func<bool> mutate)
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isShutDown || !mutate())
            {
                return;
            }

            PruneFinished();
            string json = JsonSerializer.Serialize(BuildDocument(), _serializerOptions);
            await File.WriteAllTextAsync(_tempPath, json).ConfigureAwait(false);
            await ReplaceAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 次の状態変化か heartbeat で全体を書き直すため、ここでは記録だけ残す
            await _loggingService.WriteAsync(
                $"[ReviewStatus] 公開状態を書き出せません: {_statusPath}: {ex.Message}").ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReplaceAsync()
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(_tempPath, _statusPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < _maxReplaceAttempts && ex is IOException or UnauthorizedAccessException)
            {
                // 読み取り側が削除共有なしで開いている間は置換が共有違反になる。読み取りは数ミリ秒で終わるため短く待つ
                await Task.Delay(_replaceRetryDelay).ConfigureAwait(false);
            }
        }
    }

    // 公開する期間と件数を超えた終了は、メモリにも残さない
    private void PruneFinished()
    {
        DateTimeOffset cutoff = _timeProvider.GetUtcNow() - ReviewStatusDocumentBuilder.RecentRetention;
        _finished.RemoveAll(entry => entry.FinishedAt < cutoff);
        if (_finished.Count > ReviewStatusDocumentBuilder.RecentLimit)
        {
            ReviewStatusEntry[] keep = [.. _finished
                .OrderByDescending(static entry => entry.FinishedAt)
                .Take(ReviewStatusDocumentBuilder.RecentLimit)];
            _finished.Clear();
            _finished.AddRange(keep);
        }
    }

    private ReviewStatusDocument BuildDocument()
        => ReviewStatusDocumentBuilder.Build(
            _current.Values.Concat(_finished),
            new ReviewStatusEnvironment(
                _appVersion,
                _startedAt,
                _subscriptionState,
                _subscriptionSince,
                _isAutoStartEnabled()),
            _timeProvider.GetUtcNow());
}
