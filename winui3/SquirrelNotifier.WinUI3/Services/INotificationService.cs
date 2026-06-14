using System;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

internal interface INotificationService
{
    void Initialize();

    void NotifyReviewEventReceived(string? message, string? recommendedNextAction);

    void NotifyReviewEvent(ReviewEvent reviewEvent);

    event EventHandler<ReviewEvent>? ReviewEventReceived;

    event EventHandler? OpenAppRequested;
}
