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

    public void NotifyUpdateAvailable(string current, string latest)
    {
        try
        {
            AppNotificationBuilder builder = new AppNotificationBuilder()
                .AddText("WSL2 kernel update available")
                .AddText($"Current: {current}")
                .AddText($"Latest: {latest}")
                .AddButton(new AppNotificationButton("Open release page")
                    .AddArgument("action", "open_release")
                    .AddArgument("version", latest));

            AppNotification notification = builder.BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch
        {
            // Fallback could be added here (e.g., message dialog) if AppNotification identity is unavailable.
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (!args.Arguments.TryGetValue("action", out string? action))
        {
            return;
        }

        if (action == "open_release" && args.Arguments.TryGetValue("version", out string? version))
        {
            string url = $"https://github.com/microsoft/WSL2-Linux-Kernel/releases/tag/{version}";
            TryOpenUrl(url);
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
