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

    public TrayIconService(TaskbarIcon taskbarIcon)
    {
        _taskbarIcon = taskbarIcon ?? throw new ArgumentNullException(nameof(taskbarIcon));
        _taskbarIcon.PopupPlacement = PlacementMode.Bottom;
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

    public void ShowNotification(
        string title,
        string message,
        H.NotifyIcon.Core.NotificationIcon icon = H.NotifyIcon.Core.NotificationIcon.None)
    {
        _taskbarIcon.ShowNotification(title, message, icon, sound: true, respectQuietTime: true);
    }

    public void ShowReviewPopup()
    {
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
