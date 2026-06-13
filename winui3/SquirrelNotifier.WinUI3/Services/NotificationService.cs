// <copyright file="NotificationService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace SquirrelNotifier.WinUI3.Services;

[ExcludeFromCodeCoverage]
internal sealed class NotificationService
{
    private bool _initialized;

    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
        _initialized = true;
    }

    public void NotifyReviewEventReceived(string? message, string? recommendedNextAction)
    {
        try
        {
            AppNotificationBuilder builder = new AppNotificationBuilder()
                .AddText("New Review Event Received")
                .AddText(message ?? "A review event has occurred.")
                .AddText(recommendedNextAction ?? "Check the gateway/repository.");

            AppNotification notification = builder.BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch
        {
            // Fallback could be added here
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
    }

    private static void TryOpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
            // ignore
        }
    }
}
