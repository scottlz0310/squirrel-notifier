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
    /// <returns>通知に表示する概要文.</returns>
    public static string BuildSummary(ReviewEvent reviewEvent, bool isAutoStarted)
    {
        return isAutoStarted
            ? $"自動でレビューを開始しました: {reviewEvent.Repository}#{reviewEvent.PrNumber}"
            : $"{reviewEvent.Reason}: {reviewEvent.Repository}#{reviewEvent.PrNumber}";
    }
}
