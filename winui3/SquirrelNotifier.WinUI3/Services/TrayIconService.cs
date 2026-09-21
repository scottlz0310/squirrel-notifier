// <copyright file="TrayIconService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using H.NotifyIcon;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;

namespace SquirrelNotifier.WinUI3.Services;

[ExcludeFromCodeCoverage]
internal sealed class TrayIconService : IDisposable
{
    private readonly TaskbarIcon _taskbarIcon;
    private readonly TrayNotificationCoordinator _notificationCoordinator;

    public TrayIconService(TaskbarIcon taskbarIcon, Func<string, Task> writeLogAsync)
    {
        _taskbarIcon = taskbarIcon ?? throw new ArgumentNullException(nameof(taskbarIcon));
        ArgumentNullException.ThrowIfNull(writeLogAsync);
        _taskbarIcon.PopupPlacement = PlacementMode.Bottom;
        _notificationCoordinator = new TrayNotificationCoordinator(
            notification => _taskbarIcon.ShowNotification(
                notification.Title,
                notification.Message,
                notification.Icon,
                sound: true,
                respectQuietTime: true),
            writeLogAsync);
    }

    public void UpdateIcon(string iconFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iconFileName);
        _taskbarIcon.IconSource = new BitmapImage(new Uri($"ms-appx:///Assets/{iconFileName}"));
    }

    public void UpdateTooltip(string tooltip)
    {
        _taskbarIcon.ToolTipText = tooltip;
    }

    public void ApplyNotification(
        SubscriptionState state,
        TrayNotificationPresentation? notification,
        string? notificationLogMessage)
    {
        _notificationCoordinator.Apply(state, notification, notificationLogMessage);
    }

    public void MarkReady()
    {
        _notificationCoordinator.MarkReady();
    }

    public void ShowNotification(
        string title,
        string message,
        H.NotifyIcon.Core.NotificationIcon icon = H.NotifyIcon.Core.NotificationIcon.None)
    {
        _taskbarIcon.ShowNotification(title, message, icon, sound: true, respectQuietTime: true);
    }

    public void ShowReviewPopup()
    {
        // PopupPlacement.Bottom では H.NotifyIcon がシステムトレイの位置を解決するため、引数の座標は使用されない。
        _taskbarIcon.ShowTrayPopup(System.Drawing.Point.Empty);
    }

    public void CloseReviewPopup()
    {
        _taskbarIcon.CloseTrayPopup();
    }

    public void Dispose()
    {
        _taskbarIcon.Dispose();
    }
}
