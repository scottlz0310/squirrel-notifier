// <copyright file="AgentExecutionWindow.xaml.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;
using SquirrelNotifier.WinUI3.ViewModels;

namespace SquirrelNotifier.WinUI3;

/// <summary>
/// エージェント実行セッションの進捗とログをリアルタイム表示する小型サブウィンドウ（#144）。
/// 表示ロジックは <see cref="AgentExecutionViewModel"/> に分離しており、本クラスは
/// イベント購読・DispatcherQueue へのバッチ反映・配置・lifecycle のみを担う.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed partial class AgentExecutionWindow : Window
{
    private const int _windowWidthLogical = 480;
    private const int _windowHeightLogical = 380;
    private const int _placementMarginPhysical = 16;
    private static readonly TimeSpan _autoCloseDelay = TimeSpan.FromSeconds(3);

    private readonly AgentExecutionSession _session;
    private readonly Action _cancelAction;
    private readonly CancellationTokenSource _readCts = new();
    private readonly object _pendingLock = new();
    private readonly List<AgentExecutionEvent> _pending = new();
    private readonly Microsoft.UI.Windowing.AppWindow _appWindow;
    private bool _flushScheduled;
    private bool _terminalHandled;
    private bool _isClosed;
    private DispatcherQueueTimer? _autoCloseTimer;

    public AgentExecutionWindow(AgentExecutionSession session, AgentExecutionViewModel viewModel, Action cancelAction)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(cancelAction);

        _session = session;
        ViewModel = viewModel;
        _cancelAction = cancelAction;

        InitializeComponent();

        // Window 直下では x:Bind Mode=OneWay が使えない（Window は FrameworkElement ではない）ため、
        // 静的な初期値はここで設定し、動的な更新は FlushPendingEvents が一括で反映する
        Title = viewModel.Title;
        TitleText.Text = viewModel.Title;
        StatusTextBlock.Text = viewModel.StatusText;
        LogListView.ItemsSource = viewModel.LogLines;

        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        ConfigurePlacement(hwnd, windowId);

        Closed += OnWindowClosed;

        _ = ConsumeEventsAsync();
    }

    internal AgentExecutionViewModel ViewModel { get; }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    // 現在モニターの work area 内・右下へ DPI を考慮した物理ピクセルで配置する
    private void ConfigurePlacement(nint hwnd, Microsoft.UI.WindowId windowId)
    {
        double scale = GetDpiForWindow(hwnd) / 96.0;
        int width = (int)(_windowWidthLogical * scale);
        int height = (int)(_windowHeightLogical * scale);

        var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
        Windows.Graphics.RectInt32 workArea = displayArea.WorkArea;
        (int x, int y) = WindowPlacementCalculator.CalculateBottomRight(
            workArea.X, workArea.Y, workArea.Width, workArea.Height, width, height, _placementMarginPhysical);

        _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
    }

    private async Task ConsumeEventsAsync()
    {
        try
        {
            await foreach (AgentExecutionEvent executionEvent in _session.ReadEventsAsync(_readCts.Token).ConfigureAwait(false))
            {
                bool schedule;
                lock (_pendingLock)
                {
                    _pending.Add(executionEvent);
                    schedule = !_flushScheduled;
                    _flushScheduled = true;
                }

                if (schedule)
                {
                    // UI 更新のバッチ化: flush 予約中に届いたイベントは同じ flush でまとめて反映される
                    DispatcherQueue.TryEnqueue(FlushPendingEvents);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // ウィンドウクローズによる購読中断。実行自体のキャンセルは OnWindowClosed が担う
        }
    }

    private void FlushPendingEvents()
    {
        if (_isClosed)
        {
            return;
        }

        List<AgentExecutionEvent> batch;
        lock (_pendingLock)
        {
            batch = new List<AgentExecutionEvent>(_pending);
            _pending.Clear();
            _flushScheduled = false;
        }

        ViewModel.ApplyBatch(batch);

        PhaseProgressBar.IsIndeterminate = ViewModel.IsIndeterminate;
        PhaseProgressBar.Value = ViewModel.ProgressValue;
        StatusTextBlock.Text = ViewModel.StatusText;
        CancelButton.IsEnabled = ViewModel.IsRunning;

        if (ViewModel.LogLines.Count > 0)
        {
            LogListView.ScrollIntoView(ViewModel.LogLines[^1]);
        }

        if (ViewModel.IsCompleted && !_terminalHandled)
        {
            _terminalHandled = true;
            ShowTerminalState();
        }
    }

    private void ShowTerminalState()
    {
        ResultInfoBar.Severity = ViewModel.Outcome switch
        {
            AgentExecutionOutcome.Succeeded => InfoBarSeverity.Success,
            AgentExecutionOutcome.Cancelled or AgentExecutionOutcome.TimedOut => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Error,
        };
        ResultInfoBar.Title = ViewModel.StatusText;
        ResultInfoBar.Message = ViewModel.Verdict is string verdict ? $"Verdict: {verdict}" : string.Empty;
        ResultInfoBar.IsOpen = true;

        // 成功時のみ短い猶予の後に自動クローズ（設定で無効化可能）。失敗時は診断のため保持する
        if (ViewModel.ShouldAutoClose)
        {
            _autoCloseTimer = DispatcherQueue.CreateTimer();
            _autoCloseTimer.Interval = _autoCloseDelay;
            _autoCloseTimer.IsRepeating = false;
            _autoCloseTimer.Tick += (_, _) =>
            {
                if (!_isClosed)
                {
                    Close();
                }
            };
            _autoCloseTimer.Start();
        }
    }

    private void OnPinToggleClick(object sender, RoutedEventArgs e)
    {
        if (_appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = PinToggle.IsChecked == true;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
        => _cancelAction();

    private void OnCloseClick(object sender, RoutedEventArgs e)
        => Close();

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        _autoCloseTimer?.Stop();

        // 実行中にウィンドウを閉じた場合は実行もキャンセルする（旧・実行中ダイアログの
        // 「キャンセル」ボタンと同じ挙動。バックグラウンドで実行を継続させない）
        if (ViewModel.IsRunning)
        {
            _cancelAction();
        }

        _readCts.Cancel();
        _readCts.Dispose();
    }
}
