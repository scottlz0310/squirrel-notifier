// <copyright file="ReviewObservationPublisher.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>観測の送信の設定（実行時に読み直す）.</summary>
/// <param name="Enabled">送信するか。既定は無効.</param>
/// <param name="ProducerId">送信主体の ID。review-raven の保護設定で、サービス主体に割り当てられた ID.</param>
internal sealed record ReviewObservationPublisherOptions(bool Enabled, string ProducerId);

/// <summary>
/// reviewer の起動待ち・実行中・終了の観測を、review-raven へ送る（squirrel-notifier#497 の S1）.
/// review-raven 経由の候補（依頼 cycle との対応を持つ項目）だけを送り、旧経路の候補は送らない.
/// 状態が変わったとき、および heartbeat のとき（起動待ち・実行中は、同じ状態でも送って鮮度を保つ）に送る.
/// </summary>
/// <remarks>
/// 観測は、レビューの結果の正本ではない。送信の失敗・遅れは、reviewer の起動、公開状態（<c>review-status.json</c>）、
/// アプリの動作を止めない。失敗した項目は、次の変化または heartbeat のときに、そのときの最新の状態を送り直す（古い状態は送らない）.
/// 送り直しても成功しない失敗（割当外・矛盾・設定の誤り）は、その cycle の送信をやめる.
/// </remarks>
internal sealed class ReviewObservationPublisher : IAsyncDisposable
{
    private readonly Func<Task<IReadOnlyList<ReviewStatusEntry>>> _getEntries;
    private readonly IObservationSender _sender;
    private readonly Func<ReviewObservationPublisherOptions> _getOptions;
    private readonly Func<bool> _isAutoStartEnabled;
    private readonly long _producerEpoch;
    private readonly LoggingService _loggingService;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // cycle ごとの送信の経過。sequence は、同じ cycle・世代の中で増やす（review-raven が cycle ごとに順序を検証する）
    private readonly Dictionary<string, CycleProgress> _progress = new(StringComparer.Ordinal);
    private int _rerunRequested;
    private int _heartbeatRequested;
    private bool _isShutDown;
    private string? _loggedInvalidProducerId;

    public ReviewObservationPublisher(
        Func<Task<IReadOnlyList<ReviewStatusEntry>>> getEntries,
        IObservationSender sender,
        Func<ReviewObservationPublisherOptions> getOptions,
        Func<bool> isAutoStartEnabled,
        long producerEpoch,
        LoggingService loggingService,
        TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(producerEpoch, 1);

        _getEntries = getEntries ?? throw new ArgumentNullException(nameof(getEntries));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _getOptions = getOptions ?? throw new ArgumentNullException(nameof(getOptions));
        _isAutoStartEnabled = isAutoStartEnabled ?? throw new ArgumentNullException(nameof(isAutoStartEnabled));
        _producerEpoch = producerEpoch;
        _loggingService = loggingService ?? throw new ArgumentNullException(nameof(loggingService));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// <see cref="ReviewStatusService.EntriesChanged"/> のハンドラー。送信は別のタスクで行い、呼び出し元を待たせない.
    /// </summary>
    /// <param name="sender">発火元.</param>
    /// <param name="e">状態の変化.</param>
    internal void OnEntriesChanged(object? sender, ReviewStatusChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        _ = RunAsync(e.IsHeartbeat);
    }

    /// <summary>
    /// 送信を 1 回行う。実行中の送信があれば、その終了後に最新の状態でもう 1 回行うよう予約して戻る
    /// （通知が重なっても、送信は同時に 1 つだけで、最後の状態が必ず送られる）.
    /// </summary>
    /// <param name="isHeartbeat">heartbeat による起動か.</param>
    /// <returns>送信（または予約）が完了したタスク.</returns>
    internal async Task RunAsync(bool isHeartbeat)
    {
        Interlocked.Exchange(ref _rerunRequested, 1);
        if (isHeartbeat)
        {
            Interlocked.Exchange(ref _heartbeatRequested, 1);
        }

        while (true)
        {
            if (!await _gate.WaitAsync(0).ConfigureAwait(false))
            {
                // 実行中の送信が、予約を見て、もう 1 回行う
                return;
            }

            try
            {
                while (Interlocked.Exchange(ref _rerunRequested, 0) == 1)
                {
                    bool heartbeat = Interlocked.Exchange(ref _heartbeatRequested, 0) == 1;
                    await PassAsync(heartbeat, stopped: false, CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }

            // 解放の直前に届いた予約を取りこぼさない
            if (Volatile.Read(ref _rerunRequested) == 0)
            {
                return;
            }
        }
    }

    /// <summary>
    /// アプリの終了を review-raven へ知らせる（起動待ち・実行中の項目に <c>stopped=true</c> を送る）。
    /// 終了を遅らせないよう、待つのは <paramref name="timeout"/> まで。以降は送信しない.
    /// </summary>
    /// <param name="timeout">待つ時間の上限.</param>
    /// <returns>完了したタスク.</returns>
    public async Task ShutdownAsync(TimeSpan timeout)
    {
        using CancellationTokenSource cts = new(timeout, _timeProvider);
        try
        {
            await _gate.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _isShutDown = true;
            await _loggingService.WriteAsync("[Observation] 終了の通知を送れませんでした（実行中の送信が終わりませんでした）。").ConfigureAwait(false);
            return;
        }

        try
        {
            if (_isShutDown)
            {
                return;
            }

            await PassAsync(heartbeat: false, stopped: true, cts.Token).ConfigureAwait(false);
            _isShutDown = true;
        }
        catch (OperationCanceledException)
        {
            _isShutDown = true;
            await _loggingService.WriteAsync("[Observation] 終了の通知を送り切れませんでした（時間切れ）。").ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task PassAsync(bool heartbeat, bool stopped, CancellationToken cancellationToken)
    {
        ReviewObservationPublisherOptions options = _getOptions();
        if (_isShutDown || !options.Enabled)
        {
            return;
        }

        if (!ReviewObservationBuilder.IsValidProducerId(options.ProducerId))
        {
            if (!string.Equals(_loggedInvalidProducerId, options.ProducerId, StringComparison.Ordinal))
            {
                _loggedInvalidProducerId = options.ProducerId;
                await _loggingService.WriteAsync("[Observation] 送信主体の ID が契約の形式ではないため、観測を送りません。").ConfigureAwait(false);
            }

            return;
        }

        IReadOnlyList<ReviewStatusEntry> entries = await _getEntries().ConfigureAwait(false);
        bool autoStartEnabled = _isAutoStartEnabled();
        HashSet<string> live = new(StringComparer.Ordinal);

        foreach (ReviewStatusEntry entry in entries)
        {
            if (entry.Cycle is not { } cycle)
            {
                continue;
            }

            live.Add(cycle.CycleId);
            if (!_progress.TryGetValue(cycle.CycleId, out CycleProgress? progress))
            {
                progress = new CycleProgress();
                _progress[cycle.CycleId] = progress;
            }

            if (progress.GaveUp)
            {
                continue;
            }

            string fingerprint = Fingerprint(entry, autoStartEnabled);
            bool changed = !string.Equals(fingerprint, progress.AcknowledgedFingerprint, StringComparison.Ordinal);
            bool finished = entry.State == ReviewStatusState.Finished;

            // 終了は、受理されたら二度と送らない。起動待ち・実行中は、同じ状態でも heartbeat で送って鮮度を保つ
            bool send = stopped
                ? !finished
                : changed || progress.Dirty || (heartbeat && !finished);
            if (!send)
            {
                continue;
            }

            ReviewObservation? observation = ReviewObservationBuilder.Build(
                entry,
                autoStartEnabled,
                options.ProducerId,
                _producerEpoch,
                progress.NextSequence++,
                _timeProvider.GetUtcNow(),
                stopped);
            if (observation is null)
            {
                continue;
            }

            ObservationSendResult result = await SendSafelyAsync(observation, cancellationToken).ConfigureAwait(false);
            await ApplyResultAsync(cycle.CycleId, progress, fingerprint, result).ConfigureAwait(false);
        }

        // 一覧から消えた（整理された）cycle の経過は持たない
        foreach (string gone in _progress.Keys.Where(key => !live.Contains(key)).ToArray())
        {
            _progress.Remove(gone);
        }
    }

    private async Task<ObservationSendResult> SendSafelyAsync(ReviewObservation observation, CancellationToken cancellationToken)
    {
        try
        {
            return await _sender.SendAsync(observation, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 送信の失敗で、アプリや reviewer の起動を止めない。送り直せる失敗として扱う
            return ObservationSendResult.Transient("SENDER_EXCEPTION", ex.GetType().Name);
        }
    }

    private async Task ApplyResultAsync(string cycleId, CycleProgress progress, string fingerprint, ObservationSendResult result)
    {
        if (result.Success)
        {
            progress.AcknowledgedFingerprint = fingerprint;
            progress.Dirty = false;
            progress.LastFailureCode = null;
            return;
        }

        if (!result.Retryable)
        {
            progress.GaveUp = true;
            progress.Dirty = false;
            await _loggingService.WriteAsync(
                $"[Observation] cycle {cycleId} の観測を送れません。この cycle の送信をやめます: {result.ErrorCode}: {result.Message}").ConfigureAwait(false);
            return;
        }

        progress.Dirty = true;
        if (!string.Equals(progress.LastFailureCode, result.ErrorCode, StringComparison.Ordinal))
        {
            // 同じ失敗の繰り返しは記録しない（60 秒ごとのログの氾濫を避ける）
            progress.LastFailureCode = result.ErrorCode;
            await _loggingService.WriteAsync(
                $"[Observation] cycle {cycleId} の観測を送れませんでした。次の変化または heartbeat で最新の状態を送り直します: {result.ErrorCode}: {result.Message}").ConfigureAwait(false);
        }
    }

    // 送る内容（段階・保留の理由・終了結果・候補・HEAD）が同じかを見る。時刻・sequence は含めない
    private static string Fingerprint(ReviewStatusEntry entry, bool autoStartEnabled)
        => string.Join(
            '|',
            ReviewStatusDocumentBuilder.DescribeState(entry.State),
            ReviewStatusDocumentBuilder.DescribeHoldReason(entry, autoStartEnabled) ?? string.Empty,
            entry.Outcome?.ToString() ?? string.Empty,
            entry.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            entry.Cycle?.CandidateId ?? string.Empty,
            entry.Cycle?.HeadSha ?? string.Empty);

    private sealed class CycleProgress
    {
        // 次に使う sequence（0 から）
        internal long NextSequence { get; set; }

        // review-raven が最後に受理した内容
        internal string? AcknowledgedFingerprint { get; set; }

        // 前回の送信が失敗し、次の機会に送り直す
        internal bool Dirty { get; set; }

        // 送り直しても成功しないと分かり、送信をやめた
        internal bool GaveUp { get; set; }

        // 直前に記録した失敗の種類
        internal string? LastFailureCode { get; set; }
    }
}
