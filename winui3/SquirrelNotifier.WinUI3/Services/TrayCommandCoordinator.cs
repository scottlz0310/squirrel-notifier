// <copyright file="TrayCommandCoordinator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Threading.Tasks;
using SquirrelNotifier.WinUI3.Helpers;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>トレイメニューで選択されたコマンドを各処理へ振り分ける.</summary>
internal sealed class TrayCommandCoordinator
{
    private readonly Action _openWindow;
    private readonly Action _startSubscription;
    private readonly Func<Task> _stopSubscriptionAsync;
    private readonly Func<Task> _checkForUpdatesAsync;
    private readonly Action _exitApplication;

    public TrayCommandCoordinator(
        Action openWindow,
        Action startSubscription,
        Func<Task> stopSubscriptionAsync,
        Func<Task> checkForUpdatesAsync,
        Action exitApplication)
    {
        _openWindow = openWindow;
        _startSubscription = startSubscription;
        _stopSubscriptionAsync = stopSubscriptionAsync;
        _checkForUpdatesAsync = checkForUpdatesAsync;
        _exitApplication = exitApplication;
    }

    /// <summary>選択されたトレイコマンドを実行する。未選択や区切り線は何もしない.</summary>
    /// <param name="command">トレイメニューから返されたコマンド.</param>
    /// <returns>コマンドの非同期処理.</returns>
    public async Task ExecuteAsync(TrayMenuCommand? command)
    {
        switch (command)
        {
            case TrayMenuCommand.Open:
                _openWindow();
                break;
            case TrayMenuCommand.Start:
                _startSubscription();
                break;
            case TrayMenuCommand.Stop:
                await _stopSubscriptionAsync();
                break;
            case TrayMenuCommand.CheckForUpdates:
                await _checkForUpdatesAsync();
                break;
            case TrayMenuCommand.Exit:
                _exitApplication();
                break;
            case null:
            case TrayMenuCommand.Separator:
            default:
                break;
        }
    }
}
