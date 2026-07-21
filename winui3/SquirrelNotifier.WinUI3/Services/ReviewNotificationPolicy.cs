// <copyright file="ReviewNotificationPolicy.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Services;

internal static class ReviewNotificationPolicy
{
    // 現行 queue の3イベントはいずれも、次のアクションが reviewer side になる（#127）。
    public static bool ShouldOfferReviewerAction(string reason)
        => reason is "opened" or "synchronized" or "re-review-requested";
}
