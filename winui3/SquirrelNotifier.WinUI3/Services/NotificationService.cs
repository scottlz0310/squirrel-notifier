// <copyright file="NotificationService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

internal sealed class NotificationService : INotificationService
{
    public event EventHandler<ReviewEvent>? ReviewEventReceived;

    public event EventHandler<NotificationMessage>? NotificationRequested;

    public void NotifyReviewEvent(ReviewEvent reviewEvent)
    {
        ArgumentNullException.ThrowIfNull(reviewEvent);

        ReviewEventReceived?.Invoke(this, reviewEvent);
    }

    public void NotifyRateLimitReset(string label)
    {
        NotificationRequested?.Invoke(
            this,
            new NotificationMessage("レートリミット解除", $"{label} の制限が解除されました。"));
    }
}
