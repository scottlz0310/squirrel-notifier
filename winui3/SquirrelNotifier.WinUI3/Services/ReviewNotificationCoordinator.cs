// <copyright file="ReviewNotificationCoordinator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// レビュー通知の表示経路と、ポップアップ失敗時のフォールバックを管理する.
/// </summary>
internal sealed class ReviewNotificationCoordinator
{
    private readonly Action<ReviewEvent, bool> _showPopup;
    private readonly Action<ReviewEvent, bool> _showBalloon;
    private readonly Func<string, Task> _writeLogAsync;
    private bool _isPopupAvailable;

    internal ReviewNotificationCoordinator(
        Action<ReviewEvent, bool> showPopup,
        Action<ReviewEvent, bool> showBalloon,
        Func<string, Task> writeLogAsync)
    {
        ArgumentNullException.ThrowIfNull(showPopup);
        ArgumentNullException.ThrowIfNull(showBalloon);
        ArgumentNullException.ThrowIfNull(writeLogAsync);
        _showPopup = showPopup;
        _showBalloon = showBalloon;
        _writeLogAsync = writeLogAsync;
    }

    public void AttachPopup(Action attachPopup)
    {
        ArgumentNullException.ThrowIfNull(attachPopup);

        try
        {
            attachPopup();
            _isPopupAvailable = true;
        }
        catch (Exception ex)
        {
            _isPopupAvailable = false;
            _ = _writeLogAsync(
                $"[UI] Failed to attach tray popup: {ex.Message}. レビュー通知はバルーン通知で表示します。");
        }
    }

    public void Show(ReviewEvent reviewEvent, bool isAutoStarted)
    {
        if (!_isPopupAvailable)
        {
            ShowFallback(reviewEvent, isAutoStarted);
            return;
        }

        try
        {
            _showPopup(reviewEvent, isAutoStarted);
        }
        catch (Exception ex)
        {
            _ = _writeLogAsync(
                $"[UI] Failed to show review popup: {ex.Message}");
            ShowFallback(reviewEvent, isAutoStarted);
        }
    }

    public void ShowFallback(ReviewEvent reviewEvent, bool isAutoStarted)
        => _showBalloon(reviewEvent, isAutoStarted);
}
