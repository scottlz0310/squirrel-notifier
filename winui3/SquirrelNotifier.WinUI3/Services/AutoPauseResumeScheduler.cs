// <copyright file="AutoPauseResumeScheduler.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Threading;
using SquirrelNotifier.WinUI3.Helpers;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// Auto-Pause で保留したレビュー（#340）の再評価契機を作る。
/// <see cref="AutoPauseGate"/> は起動試行・レートリミットの更新・セッション終了でしか再評価されず、
/// 無人運用では Auto-Pause の解除を誰も確認しない。保留したときだけリセット時刻へタイマーを置き、
/// 通過後に再評価を促す.
/// </summary>
/// <remarks>
/// 予約は一発のタイマー 1 本だけで、<see cref="Schedule"/> は既存の予約を置き換える。
/// 再評価してもまだ Auto-Pause なら保留側が再び <see cref="Schedule"/> を呼ぶため自己継続し、
/// 保留が無くなれば次の予約が入らず自然に止まる.
/// </remarks>
internal sealed class AutoPauseResumeScheduler : IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();
    private ITimer? _timer;
    private bool _isDisposed;

    public AutoPauseResumeScheduler(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>再評価すべき時刻に達したときに発生する。タイマーのスレッドで発生する.</summary>
    public event EventHandler? RetryDue;

    /// <summary>
    /// 次の再評価を予約する。既存の予約は破棄する.
    /// </summary>
    /// <param name="resetAt">Auto-Pause の根拠となった limit のリセット時刻。取得できない場合は <see langword="null"/>.</param>
    public void Schedule(DateTimeOffset? resetAt)
    {
        TimeSpan delay = AutoPauseResumePolicy.ResolveRetryDelay(resetAt, _timeProvider.GetUtcNow());

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

    public void Dispose()
    {
        lock (_lock)
        {
            _isDisposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void OnTimerElapsed(object? state) => RetryDue?.Invoke(this, EventArgs.Empty);
}
