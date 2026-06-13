// <copyright file="INotificationService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Services;

internal interface INotificationService
{
    void Initialize();

    void NotifyReviewEventReceived(string? message, string? recommendedNextAction);
}
