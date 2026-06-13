// <copyright file="NotificationService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

[ExcludeFromCodeCoverage]
internal sealed class NotificationService : INotificationService
{
    private bool _initialized;

    public event EventHandler<ReviewEvent>? ReviewEventReceived;

    public event EventHandler? OpenAppRequested;

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

    public void NotifyReviewEvent(ReviewEvent reviewEvent)
    {
        ArgumentNullException.ThrowIfNull(reviewEvent);

        try
        {
            AppNotificationBuilder builder = new AppNotificationBuilder()
                .AddText($"{reviewEvent.Reason}: {reviewEvent.Repository}#{reviewEvent.PrNumber}")
                .AddText(reviewEvent.Message);

            if (UrlValidator.IsSafeGitHubUrl(reviewEvent.PrUrl, reviewEvent.Repository, reviewEvent.PrNumber))
            {
                builder.AddButton(new AppNotificationButton("PRを開く")
                    .AddArgument("action", "openUrl")
                    .AddArgument("url", reviewEvent.PrUrl));
            }

            builder.AddButton(new AppNotificationButton("アプリを開く")
                .AddArgument("action", "openApp"));

            AppNotification notification = builder.BuildNotification();
            AppNotificationManager.Default.Show(notification);

            ReviewEventReceived?.Invoke(this, reviewEvent);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to show AppNotification: {ex.Message}", ex);
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (args.Arguments.TryGetValue("action", out string? action))
        {
            if (action == "openUrl" && args.Arguments.TryGetValue("url", out string? url))
            {
                if (UrlValidator.IsSafeGitHubUrl(url))
                {
                    TryOpenUrl(url);
                }
            }
            else if (action == "openApp")
            {
                OpenAppRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        else
        {
            // Default action (body click) opens app
            OpenAppRequested?.Invoke(this, EventArgs.Empty);
        }
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
