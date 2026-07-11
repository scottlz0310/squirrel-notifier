// <copyright file="RateLimitDeltaCalculator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Collections.Generic;
using System.Linq;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// レビューセッション開始／終了時点の 2 つの <see cref="RateLimitSnapshot"/> から、
/// limit ごとの使用率 Delta を算出する（#145）。すべての「算出不可」経路は例外ではなく
/// <see cref="RateLimitDeltaResult.UnavailableReason"/> による正常系として扱う.
/// </summary>
internal sealed class RateLimitDeltaCalculator
{
    private readonly TimeProvider _timeProvider;

    public RateLimitDeltaCalculator(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// 開始・終了スナップショットから limit ごとの Delta を算出する.
    /// </summary>
    /// <param name="start">セッション開始時点のスナップショット。取得できていない場合は <see langword="null"/>.</param>
    /// <param name="end">セッション終了時点のスナップショット。取得できていない場合は <see langword="null"/>.</param>
    /// <param name="startCapturedAt">
    /// 開始スナップショットを取得しようとした時刻（レビューセッション開始時刻）。この時刻を基準に
    /// <paramref name="start"/> の鮮度を判定する。終了スナップショットは <see cref="TimeProvider"/>
    /// が返す現在時刻（= 通常このメソッドを呼ぶ直前に取得された時刻）を基準にする。この 2 つを
    /// 使い分けないと、既定 30 分の launcher timeout に対し既定 15 分の freshness 閾値では、
    /// レビューサイクルの長さそのもので開始スナップショットが常に stale 判定されてしまう
    /// （レビュー対応）.
    /// </param>
    /// <param name="freshnessThreshold">スナップショットを fresh とみなす経過時間の上限.</param>
    /// <returns>limit ごとの算出結果一覧。対象 limit が無い場合は空.</returns>
    public IReadOnlyList<RateLimitDeltaResult> Compute(RateLimitSnapshot? start, RateLimitSnapshot? end, DateTimeOffset startCapturedAt, TimeSpan freshnessThreshold)
    {
        if (end == null)
        {
            return BuildUnavailable(start?.Limits, RateLimitDeltaUnavailableReason.MissingEndSnapshot);
        }

        if (start == null)
        {
            return BuildUnavailable(end.Limits, RateLimitDeltaUnavailableReason.MissingStartSnapshot);
        }

        if (!RateLimitFreshnessPolicy.IsFresh(start.ObservedAt, startCapturedAt, freshnessThreshold))
        {
            return BuildUnavailable(end.Limits, RateLimitDeltaUnavailableReason.StartSnapshotStale);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (!RateLimitFreshnessPolicy.IsFresh(end.ObservedAt, now, freshnessThreshold))
        {
            return BuildUnavailable(end.Limits, RateLimitDeltaUnavailableReason.EndSnapshotStale);
        }

        // 重複 id が渡された場合でも例外を送出せず、最初に出現したエントリを採用する（レビュー対応）。
        // 「算出不可は例外ではなく正常系」という本クラスの契約は、外部入力（parse 結果や将来の
        // 呼び出し元）の品質に関わらず維持する.
        Dictionary<string, RateLimitInfo> startById = start.Limits
            .GroupBy(l => l.Id)
            .ToDictionary(g => g.Key, g => g.First());
        var results = new List<RateLimitDeltaResult>();

        foreach (RateLimitInfo endLimit in end.Limits)
        {
            if (!startById.TryGetValue(endLimit.Id, out RateLimitInfo? startLimit))
            {
                results.Add(new RateLimitDeltaResult(endLimit.Id, endLimit.Label, null, RateLimitDeltaUnavailableReason.LimitMissingInStart));
                continue;
            }

            // resetAt が変化している = 開始・終了の間でリセットが発生している。単純な差分は
            // 負値や実態と無関係な大量消費として誤表示されるため、算出しない.
            if (startLimit.ResetAt != endLimit.ResetAt)
            {
                results.Add(new RateLimitDeltaResult(endLimit.Id, endLimit.Label, null, RateLimitDeltaUnavailableReason.ResetBoundaryCrossed));
                continue;
            }

            if (startLimit.UsedPercentage is not double startPct || endLimit.UsedPercentage is not double endPct)
            {
                results.Add(new RateLimitDeltaResult(endLimit.Id, endLimit.Label, null, RateLimitDeltaUnavailableReason.UsedPercentageMissing));
                continue;
            }

            results.Add(new RateLimitDeltaResult(endLimit.Id, endLimit.Label, endPct - startPct, RateLimitDeltaUnavailableReason.None));
        }

        return results;
    }

    private static List<RateLimitDeltaResult> BuildUnavailable(IReadOnlyList<RateLimitInfo>? limits, RateLimitDeltaUnavailableReason reason)
    {
        if (limits == null || limits.Count == 0)
        {
            return [];
        }

        return limits.Select(l => new RateLimitDeltaResult(l.Id, l.Label, null, reason)).ToList();
    }
}
