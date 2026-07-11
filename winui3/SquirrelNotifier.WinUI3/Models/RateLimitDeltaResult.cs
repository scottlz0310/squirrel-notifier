// <copyright file="RateLimitDeltaResult.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Models;

/// <summary>
/// Delta（レビューサイクル単位の使用率差分）を算出できなかった理由（#145）。
/// いずれも例外ではなく「取得不可」として正常系で扱う.
/// </summary>
internal enum RateLimitDeltaUnavailableReason
{
    /// <summary>算出できた（Delta が利用可能）.</summary>
    None,

    /// <summary>開始スナップショットが取得できなかった（ヘッドレス実行等で statusline / hook が発火しなかった場合を含む）.</summary>
    MissingStartSnapshot,

    /// <summary>終了スナップショットが取得できなかった.</summary>
    MissingEndSnapshot,

    /// <summary>開始スナップショットが freshness 閾値より古い.</summary>
    StartSnapshotStale,

    /// <summary>終了スナップショットが freshness 閾値より古い.</summary>
    EndSnapshotStale,

    /// <summary>この limit id が開始スナップショットに存在しない.</summary>
    LimitMissingInStart,

    /// <summary>この limit id が終了スナップショットに存在しない.</summary>
    LimitMissingInEnd,

    /// <summary>開始・終了いずれかで usedPercentage が欠損している（旧スキーマ payload 等）.</summary>
    UsedPercentageMissing,

    /// <summary>開始・終了間で resetAt が変化しており、リセット境界を跨いでいる.</summary>
    ResetBoundaryCrossed,

    /// <summary>開始・終了いずれかのスナップショット内で同一 limit id が複数存在し、
    /// どちらの値が正しいか判断できない（malformed payload 等）.</summary>
    DuplicateLimitId,
}

/// <summary>
/// 1 つの limit（例: claude-code の 5 時間枠）に対する Delta 算出結果（#145）.
/// </summary>
/// <param name="LimitId">limit の ID.</param>
/// <param name="Label">表示名.</param>
/// <param name="DeltaPercentage">使用率の差分（終了時点 − 開始時点）。算出不可の場合は <see langword="null"/>.</param>
/// <param name="UnavailableReason">算出できなかった理由。算出できた場合は <see cref="RateLimitDeltaUnavailableReason.None"/>.</param>
internal sealed record RateLimitDeltaResult(
    string LimitId,
    string Label,
    double? DeltaPercentage,
    RateLimitDeltaUnavailableReason UnavailableReason)
{
    /// <summary>Gets a value indicating whether delta が算出できたかどうか.</summary>
    public bool IsAvailable => UnavailableReason == RateLimitDeltaUnavailableReason.None;
}
