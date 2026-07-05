using System;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

internal interface INotificationService
{
    void Initialize();

    void NotifyReviewEventReceived(string? message, string? recommendedNextAction);

    void NotifyReviewEvent(ReviewEvent reviewEvent);

    void NotifyRateLimitReset(string label);

    event System.EventHandler<ReviewEvent>? ReviewEventReceived;

    event System.EventHandler? OpenAppRequested;

    event System.EventHandler<string>? LaunchReviewRequested;
}
