// <copyright file="SubscriptionStateCoordinator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using H.NotifyIcon.Core;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>トレイ通知の表示内容.</summary>
internal sealed record TrayNotificationPresentation(
    string Title,
    string Message,
    NotificationIcon Icon);

/// <summary>購読状態を画面とトレイへ反映するための値.</summary>
internal sealed record SubscriptionStatePresentation(
    string IconFileName,
    string Tooltip,
    string? StatusText,
    bool IsAuthenticationRequired,
    TrayNotificationPresentation? Notification,
    string StateLogMessage,
    string? NotificationLogMessage);

/// <summary>
/// 購読状態からトレイとエラー表示の内容を決める。エラー通知の一回制御も UI から分離する。
/// トレイアイコンや InfoBar の実体は保持せず、呼び出し側が表示を反映する.
/// </summary>
/// <remarks>状態を持つため、UI スレッドからの利用を前提とする.</remarks>
internal sealed class SubscriptionStateCoordinator
{
    private const string _normalIconFileName = "squirrel-notifier.ico";
    private const string _errorIconFileName = "squirrel-notifier-error.ico";
    private const string _applicationTitle = "Squirrel Notifier";

    private bool _hasShownErrorBalloon;

    /// <summary>購読状態の変化を画面・トレイへ反映する値へ変換する.</summary>
    /// <param name="state">現在の購読状態.</param>
    /// <param name="lastError">購読Serviceが保持する直近のエラー.</param>
    /// <param name="isAuthenticationRequired">認証が必要なエラーか.</param>
    /// <returns>UI へ反映する値.</returns>
    public SubscriptionStatePresentation Update(
        SubscriptionState state,
        string lastError,
        bool isAuthenticationRequired)
    {
        if (state != SubscriptionState.Error)
        {
            _hasShownErrorBalloon = false;
            return new SubscriptionStatePresentation(
                _normalIconFileName,
                _applicationTitle,
                null,
                false,
                null,
                $"[UI] Updating tray icon to normal state. State: {state}",
                null);
        }

        bool shouldShowErrorBalloon = !_hasShownErrorBalloon;
        _hasShownErrorBalloon = true;

        TrayNotificationPresentation? notification = null;
        string? notificationLogMessage = null;
        if (shouldShowErrorBalloon)
        {
            string notificationMessage = isAuthenticationRequired
                ? lastError
                : $"接続エラー: {lastError}";
            notification = new TrayNotificationPresentation(
                _applicationTitle,
                notificationMessage,
                NotificationIcon.Error);
            notificationLogMessage = isAuthenticationRequired
                ? "[UI] Showing authentication required balloon notification."
                : "[UI] Showing connection error balloon notification.";
        }

        return new SubscriptionStatePresentation(
            _errorIconFileName,
            $"{_applicationTitle} - Error: {lastError}",
            string.IsNullOrEmpty(lastError) ? null : $"Error: {lastError}",
            isAuthenticationRequired,
            notification,
            $"[UI] Updating tray icon to error state. Error: {lastError}",
            notificationLogMessage);
    }
}
