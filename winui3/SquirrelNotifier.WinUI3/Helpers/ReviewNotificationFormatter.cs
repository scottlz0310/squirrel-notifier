// <copyright file="ReviewNotificationFormatter.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// レビューイベントを通知に表示する概要文へ変換する。ポップアップとバルーンで同じ文言規則を共有する.
/// </summary>
internal static class ReviewNotificationFormatter
{
    /// <summary>
    /// レビューイベントの自動起動状態を含む概要文を返す.
    /// </summary>
    /// <param name="reviewEvent">表示対象のレビューイベント.</param>
    /// <param name="isAutoStarted">自動起動が成功したイベントかどうか.</param>
    /// <param name="holdReason">
    /// 再評価まで保留した理由（#339/#340）。無人運用では通知が唯一の気づく手段になるため、
    /// 見送っただけのイベントと区別できる文言にする。保留していない場合は <see langword="null"/>.
    /// </param>
    /// <returns>通知に表示する概要文.</returns>
    public static string BuildSummary(ReviewEvent reviewEvent, bool isAutoStarted, string? holdReason = null)
    {
        if (isAutoStarted)
        {
            return $"自動でレビューを開始しました: {reviewEvent.Repository}#{reviewEvent.PrNumber}";
        }

        return holdReason is null
            ? $"{reviewEvent.Reason}: {reviewEvent.Repository}#{reviewEvent.PrNumber}"
            : $"レビューを保留中（{holdReason}）: {reviewEvent.Repository}#{reviewEvent.PrNumber}";
    }
}
