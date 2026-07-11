// <copyright file="RateLimitFreshnessPolicy.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// レートリミットスナップショットの鮮度判定（#145）。Delta 計算と Auto-Pause（#147）の
/// どちらからも参照される、単一スナップショットに対する独立した判定ロジック.
/// </summary>
internal static class RateLimitFreshnessPolicy
{
    /// <summary>既定の鮮度閾値（分）。statusline / hook の発火間隔にはセッションによるばらつきがあるため、
    /// やや余裕を持たせた値にする.</summary>
    public const int DefaultThresholdMinutes = 15;

    /// <summary>
    /// 指定した観測時刻が現在時刻から見て fresh（閾値内）かどうかを判定する。
    /// 未来のタイムスタンプは不正なデータとみなし stale として扱う.
    /// </summary>
    /// <param name="observedAt">スナップショットの観測時刻.</param>
    /// <param name="now">判定基準時刻.</param>
    /// <param name="threshold">許容する経過時間.</param>
    /// <returns>fresh な場合 <see langword="true"/>.</returns>
    public static bool IsFresh(DateTimeOffset observedAt, DateTimeOffset now, TimeSpan threshold)
    {
        if (observedAt > now)
        {
            return false;
        }

        return now - observedAt <= threshold;
    }
}
