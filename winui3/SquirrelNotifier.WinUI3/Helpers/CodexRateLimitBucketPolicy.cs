// <copyright file="CodexRateLimitBucketPolicy.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// Codex App Server（<c>account/rateLimits/read</c>）が返す bucket の扱いを決める（#335）。
/// bucket ID と window slot の組み合わせだけで判定し、表示名や <c>windowDurationMins</c> には
/// 依存しない（枠の追加・期間変更で判定が切れないようにするため）.
/// </summary>
internal static class CodexRateLimitBucketPolicy
{
    /// <summary>全モデルが使用する通常枠の bucket ID。primary が 5 時間制限、secondary が Weekly 制限.</summary>
    public const string GeneralBucketId = "codex";

    /// <summary>Luna 専用の Weekly 予約枠の bucket ID（Luna Reserve Weekly）.</summary>
    public const string LunaReserveBucketId = "base_model_inference";

    private const string _primarySlot = "primary";
    private const string _secondarySlot = "secondary";
    private const string _excludedSuffix = "（Auto-Pause対象外）";

    /// <summary>
    /// Auto-Pause（#147）の判断材料にしてよい枠かどうかを返す。通常枠（<see cref="GeneralBucketId"/>）の
    /// primary / secondary だけを対象とし、Luna Reserve Weekly・将来追加される予約枠・
    /// 通常枠に増えた未知の slot は、用途を確認できるまで対象外にする.
    /// </summary>
    /// <param name="bucketId">App Server の <c>limitId</c>（bucket ID）.</param>
    /// <param name="slot">window slot（<c>primary</c> / <c>secondary</c>）.</param>
    /// <returns>Auto-Pause の判断材料にしてよい場合は <see langword="true"/>.</returns>
    public static bool IsAutoPauseEligible(string? bucketId, string? slot)
        => string.Equals(bucketId, GeneralBucketId, StringComparison.Ordinal)
            && slot is _primarySlot or _secondarySlot;

    /// <summary>
    /// 枠の表示ラベルを組み立てる。既知の bucket / slot はサービス上の枠名と用途を含む固定文言、
    /// 未知の組み合わせは <paramref name="windowDurationMins"/> から作る従来の期間表記へフォールバックし、
    /// Auto-Pause の対象外である事実を表示に残す.
    /// </summary>
    /// <param name="bucketId">App Server の <c>limitId</c>（bucket ID）.</param>
    /// <param name="slot">window slot（<c>primary</c> / <c>secondary</c>）.</param>
    /// <param name="windowDurationMins">枠の期間（分）。未知の組み合わせでのみ使う.</param>
    /// <returns>表示ラベル.</returns>
    public static string BuildLabel(string? bucketId, string slot, long? windowDurationMins)
    {
        if (IsAutoPauseEligible(bucketId, slot))
        {
            return slot == _primarySlot ? "5時間制限（全モデル）" : "Weekly制限（全モデル）";
        }

        if (string.Equals(bucketId, LunaReserveBucketId, StringComparison.Ordinal) && slot == _primarySlot)
        {
            return "Luna Reserve Weekly制限（Luna専用・Auto-Pause対象外）";
        }

        return $"{BuildWindowText(windowDurationMins, slot)}{_excludedSuffix}";
    }

    private static string BuildWindowText(long? windowDurationMins, string slot)
    {
        if (windowDurationMins is not long mins || mins <= 0)
        {
            return $"{slot} 枠";
        }

        if (mins % 1440 == 0)
        {
            return $"{mins / 1440}日枠";
        }

        return mins % 60 == 0 ? $"{mins / 60}時間枠" : $"{mins}分枠";
    }
}
