// <copyright file="TrayNotificationCoordinator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// トレイアイコンの初期化完了まで通知表示を保留する.
/// </summary>
internal sealed class TrayNotificationCoordinator
{
    private readonly Action<TrayNotificationPresentation> _showNotification;
    private readonly Func<string, Task> _writeLogAsync;
    private PendingNotification? _pendingNotification;
    private bool _isReady;

    public TrayNotificationCoordinator(
        Action<TrayNotificationPresentation> showNotification,
        Func<string, Task> writeLogAsync)
    {
        ArgumentNullException.ThrowIfNull(showNotification);
        ArgumentNullException.ThrowIfNull(writeLogAsync);
        _showNotification = showNotification;
        _writeLogAsync = writeLogAsync;
    }

    /// <summary>
    /// 購読状態に応じた通知を受け取り、トレイアイコンの初期化後に表示する.
    /// </summary>
    /// <param name="state">現在の購読状態.</param>
    /// <param name="notification">表示する通知.</param>
    /// <param name="notificationLogMessage">通知表示時に記録するログ.</param>
    public void Apply(
        SubscriptionState state,
        TrayNotificationPresentation? notification,
        string? notificationLogMessage)
    {
        if (state != SubscriptionState.Error)
        {
            _pendingNotification = null;
            return;
        }

        if (notification is null)
        {
            return;
        }

        _pendingNotification = new PendingNotification(notification, notificationLogMessage);
        TryShowPendingNotification();
    }

    /// <summary>
    /// トレイアイコンのネイティブリソースが生成済みであることを通知する.
    /// </summary>
    public void MarkReady()
    {
        _isReady = true;
        TryShowPendingNotification();
    }

    private void TryShowPendingNotification()
    {
        if (!_isReady || _pendingNotification is null)
        {
            return;
        }

        PendingNotification pendingNotification = _pendingNotification;
        _pendingNotification = null;

        if (pendingNotification.LogMessage is not null)
        {
            _ = _writeLogAsync(pendingNotification.LogMessage);
        }

        try
        {
            _showNotification(pendingNotification.Notification);
        }
        catch (Exception ex)
        {
            _ = _writeLogAsync($"[UI] Failed to show tray notification: {ex.Message}");
        }
    }

    private sealed record PendingNotification(
        TrayNotificationPresentation Notification,
        string? LogMessage);
}
