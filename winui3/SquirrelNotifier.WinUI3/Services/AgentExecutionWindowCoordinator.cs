// <copyright file="AgentExecutionWindowCoordinator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// エージェント実行ウィンドウの参照保持とクローズ時の状態解除を管理する.
/// </summary>
internal sealed class AgentExecutionWindowCoordinator
{
    private readonly Func<ReviewStartLaunch, IAgentExecutionWindow> _windowFactory;
    private readonly Queue<ReviewStartLaunch> _deferredLaunches = new();
    private IAgentExecutionWindow? _window;

    public AgentExecutionWindowCoordinator(Func<ReviewStartLaunch, IAgentExecutionWindow> windowFactory)
    {
        ArgumentNullException.ThrowIfNull(windowFactory);
        _windowFactory = windowFactory;
    }

    internal bool HasActiveWindow => _window is not null;

    internal int DeferredCount => _deferredLaunches.Count;

    /// <summary>
    /// 起動結果に含まれるセッションの表示ウィンドウを開く。同時に一つだけ保持し、
    /// 表示中のウィンドウがある場合は、それが閉じた時点で起動順に開く（#339）.
    /// </summary>
    /// <remarks>
    /// 前の実行が終わった直後に起動したセッションは、成功時の自動クローズの猶予中や、失敗時に診断のため
    /// 残したウィンドウと重なる。セッションのイベントは容量無制限のチャンネルに残るため、後から開いても出力を失わない.
    /// </remarks>
    /// <param name="result">レビュー起動の結果.</param>
    public void Show(ReviewStartResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Launch is not { } launch)
        {
            return;
        }

        if (_window is not null)
        {
            _deferredLaunches.Enqueue(launch);
            return;
        }

        Open(launch);
    }

    private void Open(ReviewStartLaunch launch)
    {
        IAgentExecutionWindow window = _windowFactory(launch);
        _window = window;
        window.Closed += OnWindowClosed;
        window.Activate();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(_window, sender))
        {
            return;
        }

        _window = null;
        if (_deferredLaunches.TryDequeue(out ReviewStartLaunch? launch))
        {
            Open(launch);
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
