// <copyright file="AutoPauseResumePolicy.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// Auto-Pause で保留したレビュー（#340）を、いつ再評価するかを決める。
/// Auto-Pause の解除は fresh な snapshot で使用率が危険水域を下回ったときにしか起こらないため、
/// リセット時刻を過ぎただけでは解除を確認できない。通過後に確認できなかった場合は、
/// 次の snapshot 更新を待つ一定間隔の再試行へ切り替える.
/// </summary>
internal static class AutoPauseResumePolicy
{
    /// <summary>リセット時刻が不明、または通過しても解除を確認できないときの再試行間隔.</summary>
    /// <remarks>
    /// 短くしない。再試行ごとに snapshot の取得（codex では App Server プロセスの起動）が走り、
    /// 再評価の記録で Recent activity（<c>MaxEntries</c> 行）が流れてしまう.
    /// </remarks>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(15);

    // リセット時刻の直後は snapshot がまだ更新されておらず、gate が Paused を維持する。
    // 無駄な起動試行を 1 回減らすため、わずかに過ぎてから評価する
    private static readonly TimeSpan _resetGracePeriod = TimeSpan.FromSeconds(30);

    // ITimer が受け付ける最大遅延（約 24.8 日）。異常な resetAt でも例外にしない
    private static readonly TimeSpan _maxDelay = TimeSpan.FromMilliseconds(int.MaxValue - 1);

    /// <summary>
    /// 次に再評価するまでの待ち時間を返す.
    /// </summary>
    /// <param name="resetAt">Auto-Pause の根拠となった limit のリセット時刻。取得できない場合は <see langword="null"/>.</param>
    /// <param name="now">現在時刻.</param>
    /// <returns>待ち時間.</returns>
    public static TimeSpan ResolveRetryDelay(DateTimeOffset? resetAt, DateTimeOffset now)
    {
        if (resetAt is not DateTimeOffset reset)
        {
            return RetryInterval;
        }

        TimeSpan delay = reset + _resetGracePeriod - now;
        if (delay <= TimeSpan.Zero)
        {
            return RetryInterval;
        }

        return delay > _maxDelay ? _maxDelay : delay;
    }
}
