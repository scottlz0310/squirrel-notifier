// <copyright file="WindowLifecycleCoordinator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>メインウィンドウ終了時の状態遷移と後処理の順序を管理する.</summary>
internal sealed class WindowLifecycleCoordinator
{
    private readonly Action _unsubscribeEventHandlers;
    private readonly Action _disposeOwnedResources;
    private readonly Action _closeWindow;
    private bool _isExitRequested;

    public WindowLifecycleCoordinator(
        Action unsubscribeEventHandlers,
        Action disposeOwnedResources,
        Action closeWindow)
    {
        _unsubscribeEventHandlers = unsubscribeEventHandlers ?? throw new ArgumentNullException(nameof(unsubscribeEventHandlers));
        _disposeOwnedResources = disposeOwnedResources ?? throw new ArgumentNullException(nameof(disposeOwnedResources));
        _closeWindow = closeWindow ?? throw new ArgumentNullException(nameof(closeWindow));
    }

    public bool IsExitRequested => _isExitRequested;

    /// <summary>終了処理を一度だけ実行し、後処理が完了してからウィンドウを閉じる.</summary>
    public void RequestExit()
    {
        if (_isExitRequested)
        {
            return;
        }

        _isExitRequested = true;
        _unsubscribeEventHandlers();
        _disposeOwnedResources();
        _closeWindow();
    }
}
