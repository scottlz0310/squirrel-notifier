// <copyright file="AgentExecutionWindowCoordinator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using Microsoft.UI.Xaml;
using SquirrelNotifier.WinUI3;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// エージェント実行ウィンドウの参照保持とクローズ時の状態解除を管理する.
/// </summary>
internal sealed class AgentExecutionWindowCoordinator
{
    private readonly Func<ReviewStartLaunch, IAgentExecutionWindow> _windowFactory;
    private IAgentExecutionWindow? _window;

    public AgentExecutionWindowCoordinator(Func<ReviewStartLaunch, IAgentExecutionWindow> windowFactory)
    {
        ArgumentNullException.ThrowIfNull(windowFactory);
        _windowFactory = windowFactory;
    }

    internal bool HasActiveWindow => _window is not null;

    /// <summary>
    /// 起動結果に含まれるセッションの表示ウィンドウを開く。同時に一つだけ保持する.
    /// </summary>
    /// <param name="result">レビュー起動の結果.</param>
    public void Show(ReviewStartResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Launch is not { } launch || _window is not null)
        {
            return;
        }

        IAgentExecutionWindow window = _windowFactory(launch);
        _window = window;
        window.Closed += OnWindowClosed;
        window.Activate();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (ReferenceEquals(_window, sender))
        {
            _window = null;
        }
    }
}

/// <summary>
/// WinUI Window をテスト可能な実行ウィンドウ境界へ変換する.
/// </summary>
internal interface IAgentExecutionWindow
{
    event EventHandler? Closed;

    void Activate();
}

/// <summary>
/// <see cref="AgentExecutionWindow"/> の UI イベントを Coordinator 向けに変換する.
/// </summary>
internal sealed class AgentExecutionWindowAdapter : IAgentExecutionWindow
{
    private readonly AgentExecutionWindow _window;

    public AgentExecutionWindowAdapter(AgentExecutionWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
        _window.Closed += OnWindowClosed;
    }

    public event EventHandler? Closed;

    public void Activate() => _window.Activate();

    private void OnWindowClosed(object sender, WindowEventArgs args)
        => Closed?.Invoke(this, EventArgs.Empty);
}
