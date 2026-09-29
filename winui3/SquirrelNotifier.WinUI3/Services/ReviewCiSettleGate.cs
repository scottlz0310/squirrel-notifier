// <copyright file="ReviewCiSettleGate.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// reviewer を自動起動する前に、required checks の確定を待つ薄いゲート（暫定、#456）。
/// <see cref="CiSettleWaiter"/> を呼び、その結果を <see cref="ReviewAutoStartPolicy"/> の判定へ写し、
/// 未確定の間は再評価の契機（<see cref="RetryDue"/>）を作るだけで、取得・待機の規則は持たない。
/// 保留は呼び出し側の保留キューが持ち、この契機で再評価される.
/// </summary>
/// <remarks>
/// <para>
/// 暫定の処置である。CI の待ち時間とレビューの時間が直列になるため、壁時計としては無駄が多い。
/// 恒久案では reviewer を即時に起動し、完了通知の直前に 1 回だけ CI の確定を待つ。
/// thread-owl の CI 状態 tool、または終端の待機手段が提供された時点で、このゲートと
/// <see cref="ReviewAutoStartPolicy"/> の CI 確定待ちの節を撤去し、<see cref="CiSettleWaiter"/> を転用する.
/// </para>
/// <para>判定の呼び出しは UI スレッドを前提とする。再評価の契機（<see cref="RetryDue"/>）だけがタイマーのスレッドで発生する.</para>
/// </remarks>
internal sealed class ReviewCiSettleGate : IDisposable
{
    /// <summary>
    /// 起動前の待機の間隔と上限。CI は最長 8 分の見込みで、余裕を見て 12 分とする。
    /// 恒久案の上限は別の値になり得るため、<see cref="CiSettleWaiter"/> の定数にはせず、ここ（呼び出し側）で指定する.
    /// </summary>
    public static readonly CiSettleWaitOptions WaitOptions = new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(12));

    private readonly CiSettleWaiter _waiter;
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();
    private ITimer? _timer;
    private bool _isDisposed;

    public ReviewCiSettleGate(CiSettleWaiter waiter, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(waiter);
        _waiter = waiter;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>未確定のまま次の確認の時刻に達したときに発生する。タイマーのスレッドで発生する.</summary>
    public event EventHandler? RetryDue;

    /// <summary>
    /// CI の確定を確認し、自動起動の可否を返す。未確定なら次の確認を予約する.
    /// </summary>
    /// <param name="reviewEvent">自動起動の対象のレビューイベント.</param>
    /// <param name="cancellationToken">取得を中断するトークン.</param>
    /// <returns>自動起動の可否と、残す文言.</returns>
    public async Task<CiSettleGateDecision> EvaluateAsync(ReviewEvent reviewEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reviewEvent);

        CiSettleWaitResult result = await _waiter
            .CheckAsync(reviewEvent.Repository, reviewEvent.PrNumber, WaitOptions, cancellationToken)
            .ConfigureAwait(false);
        CiSettleGateDecision decision = ReviewAutoStartPolicy.DecideCiSettle(result);
        if (decision.Action == CiSettleGateAction.Hold)
        {
            ScheduleRetry(result.RetryAfter);
        }

        return decision;
    }

    /// <summary>PR が CI の確定を待っている最中か.</summary>
    /// <param name="reviewEvent">対象のレビューイベント.</param>
    /// <returns>待機中の場合は <see langword="true"/>.</returns>
    public bool IsWaiting(ReviewEvent reviewEvent)
    {
        ArgumentNullException.ThrowIfNull(reviewEvent);
        return _waiter.IsWaiting(reviewEvent.Repository, reviewEvent.PrNumber);
    }

    /// <summary>reviewer を別の経路で起動したときなど、CI の待機が不要になった PR の状態を破棄する.</summary>
    /// <param name="reviewEvent">対象のレビューイベント.</param>
    public void Forget(ReviewEvent reviewEvent)
    {
        ArgumentNullException.ThrowIfNull(reviewEvent);
        _waiter.Forget(reviewEvent.Repository, reviewEvent.PrNumber);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _isDisposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    // 予約は一発のタイマー 1 本だけで、既存の予約を置き換える。再評価してもまだ未確定なら
    // 保留側が再び予約するため自己継続し、待機する PR が無くなれば次の予約が入らず自然に止まる
    private void ScheduleRetry(TimeSpan delay)
    {
        lock (_lock)
        {
            if (_isDisposed)
            {
                return;
            }

            _timer?.Dispose();
            _timer = _timeProvider.CreateTimer(OnTimerElapsed, null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimerElapsed(object? state) => RetryDue?.Invoke(this, EventArgs.Empty);
}
