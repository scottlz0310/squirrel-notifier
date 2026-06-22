// <copyright file="MainWindow.xaml.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;
using Windows.Foundation;
using Windows.Graphics;
using WinRT;

namespace SquirrelNotifier.WinUI3;

[ExcludeFromCodeCoverage]
internal sealed partial class MainWindow : Window
{
    private bool _isExitRequested;
    private readonly McpSubscriptionService _service;
    private readonly LoggingService _loggingService;
    private readonly SettingsService _settingsService;
    private readonly AutoUpdateService _autoUpdateService;
    private readonly ObservableCollection<string> _logEntries = new();
    private readonly ObservableCollection<Models.ReviewEvent> _reviewEvents = new();
    private readonly TrayIconService _trayIconService;
    private TrayContextMenu? _contextMenu;
    private readonly nint _hwnd;
    private readonly bool _isInitializing = true;
    private readonly INotificationService _notificationService;
    private readonly IReviewLauncherService _launcherService;
    private readonly ITaskSchedulerService _taskSchedulerService;
    private bool _isCheckingForUpdates;
    private bool _hasShownErrorBalloon;
    private bool _isAutoStartToggling;

    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    private readonly WndProcDelegate? _newWndProcDelegate;
    private readonly nint _oldWndProc;

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadImage(nint hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    private const int _gwlWndProc = -4;
    private const int _swHide = 0;
    private const int _swShow = 5;
    private const uint _wmSetIcon = 0x0080;
    private const uint _wmCommand = 0x0111;
    private const uint _iconSmall = 0;
    private const uint _iconBig = 1;
    private const uint _imageIcon = 1;
    private const uint _lrLoadFromFile = 0x00000010;

    internal MainWindow(
        McpSubscriptionService service,
        LoggingService loggingService,
        SettingsService settingsService,
        AutoUpdateService autoUpdateService,
        INotificationService notificationService,
        IReviewLauncherService launcherService,
        ITaskSchedulerService taskSchedulerService,
        bool showWindow = true)
    {
        InitializeComponent();

        // Set window size (WinUI3 requires this in code)
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new SizeInt32(520, 580));
        appWindow.Closing += OnAppWindowClosing;

        // Set window icon
        SetWindowIcon();

        _service = service;
        _loggingService = loggingService;
        _settingsService = settingsService;
        _autoUpdateService = autoUpdateService;
        _notificationService = notificationService;
        _launcherService = launcherService;
        _taskSchedulerService = taskSchedulerService;
        _service.StatusTextChanged += OnStatusTextChanged;
        _service.StateChanged += OnStateChanged;
        _loggingService.LogAppended += OnLogAppended;
        _notificationService.ReviewEventReceived += OnReviewEventReceived;
        _notificationService.LaunchReviewRequested += OnLaunchReviewRequested;
        LogList.ItemsSource = _logEntries;
        ReviewEventList.ItemsSource = _reviewEvents;

        // Load settings
        AppSettings settings = _settingsService.Settings;
        CommandPathBox.Text = settings.SubscriberCommandPath;
        ArgumentsBox.Text = settings.SubscriberArguments;
        GatewayUrlBox.Text = settings.GatewayUrl;
        ResourceUriBox.Text = settings.ResourceUri;
        TimeoutBox.Value = settings.NotificationTimeoutMs;
        LauncherPathBox.Text = settings.LauncherCommandPath;
        LauncherArgumentsBox.Text = settings.LauncherArguments;
        LauncherTimeoutBox.Value = settings.LauncherTimeoutMs;

        _isInitializing = false;

        // Check auto-start registration status
        _ = RefreshAutoStartStatusAsync();

        // Setup tray icon
        _trayIconService = new TrayIconService(this);
        _trayIconService.LeftClick += OnTrayIconLeftClick;
        _trayIconService.RightClick += OnTrayIconRightClick;
        _trayIconService.AddIcon("Squirrel Notifier");

        // Hook window messages to process tray icon messages
        _newWndProcDelegate = new WndProcDelegate(NewWndProc);
        _oldWndProc = SetWindowLongPtr(_hwnd, _gwlWndProc, Marshal.GetFunctionPointerForDelegate(_newWndProcDelegate));

        // Update control states
        UpdateControls(service.State);

        // Hide window if requested
        if (!showWindow)
        {
            ShowWindow(_hwnd, _swHide);
        }
        else
        {
            _ = CheckForUpdatesAsync(showNoUpdateDialog: false);
        }
    }

    private void OnGoToSettingsClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        SettingsExpander.IsExpanded = true;
        _ = DispatcherQueue.TryEnqueue(() => AutoStartToggle.Focus(FocusState.Programmatic));
    }

    private nint NewWndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        // Process tray icon messages
        _trayIconService?.ProcessWindowMessage(msg, wParam, lParam);

        // Process WM_COMMAND messages for context menu
        if (msg == _wmCommand)
        {
            int commandId = wParam.ToInt32() & 0xFFFF;
            if (_contextMenu?.ProcessCommand(commandId) == true)
            {
                return nint.Zero;
            }
        }

        // Call original window procedure
        return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
    }

    public void ShowWindowFromTray()
    {
        ShowWindow(_hwnd, _swShow);
        Activate();
    }

    public void HideWindowToTray()
    {
        ShowWindow(_hwnd, _swHide);
    }

    private void SetWindowIcon()
    {
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "squirrel-notifier.ico");
            if (File.Exists(iconPath))
            {
                nint hIconSmall = LoadImage(nint.Zero, iconPath, _imageIcon, 16, 16, _lrLoadFromFile);
                nint hIconBig = LoadImage(nint.Zero, iconPath, _imageIcon, 32, 32, _lrLoadFromFile);

                if (hIconSmall != nint.Zero)
                {
                    SendMessage(_hwnd, _wmSetIcon, new nint(_iconSmall), hIconSmall);
                }

                if (hIconBig != nint.Zero)
                {
                    SendMessage(_hwnd, _wmSetIcon, new nint(_iconBig), hIconBig);
                }
            }
        }
        catch
        {
            // Ignore errors when loading icon
        }
    }

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        _service.Start();
    }

    private async void OnStopClick(object sender, RoutedEventArgs e)
    {
        await _service.StopAsync();
    }

    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        _service.Start();
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        HideWindowToTray();
    }

    private void OnTrayIconLeftClick(object? sender, EventArgs e)
    {
        ShowWindowFromTray();
    }

    private void OnTrayIconRightClick(object? sender, EventArgs e)
    {
        _contextMenu?.Dispose();
        _contextMenu = new TrayContextMenu();
        _contextMenu.AddMenuItem("開く(&O)", () => DispatcherQueue.TryEnqueue(ShowWindowFromTray));
        _contextMenu.AddMenuItem("購読を開始(&S)", () => DispatcherQueue.TryEnqueue(() => _service.Start()));
        _contextMenu.AddMenuItem("購読を停止(&T)", () => DispatcherQueue.TryEnqueue(async () => await _service.StopAsync()));
        _contextMenu.AddMenuItem("アプリの更新を確認(&U)", () => DispatcherQueue.TryEnqueue(async () => await CheckForUpdatesAsync(showNoUpdateDialog: true)));
        _contextMenu.AddSeparator();
        _contextMenu.AddMenuItem("終了(&X)", () => DispatcherQueue.TryEnqueue(ExitApplication));
        _contextMenu.Show(_hwnd);
    }

    private void ExitApplication()
    {
        _isExitRequested = true;
        _service.StatusTextChanged -= OnStatusTextChanged;
        _service.StateChanged -= OnStateChanged;
        _loggingService.LogAppended -= OnLogAppended;
        _notificationService.ReviewEventReceived -= OnReviewEventReceived;
        _notificationService.LaunchReviewRequested -= OnLaunchReviewRequested;
        _trayIconService?.Dispose();
        _contextMenu?.Dispose();
        Close();
    }

    private void OnStatusTextChanged(object? sender, string message)
    {
        _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = message);
    }

    private void OnStateChanged(object? sender, SubscriptionState state)
    {
        UpdateControls(state);
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            UpdateTrayIcon(state);
            if (state == SubscriptionState.Error && !string.IsNullOrEmpty(_service.LastError))
            {
                StatusText.Text = $"Error: {_service.LastError}";
            }
        });
    }

    private void UpdateTrayIcon(SubscriptionState state)
    {
        if (state == SubscriptionState.Error)
        {
            _ = _loggingService.WriteAsync($"[UI] Updating tray icon to error state. Error: {_service.LastError}");
            _trayIconService.UpdateIcon("squirrel-notifier-error.ico");
            _trayIconService.UpdateTooltip($"Squirrel Notifier - Error: {_service.LastError}");

            if (!_hasShownErrorBalloon)
            {
                _hasShownErrorBalloon = true;
                _ = _loggingService.WriteAsync("[UI] Showing connection error balloon notification.");
                _trayIconService.ShowBalloonTip("Squirrel Notifier", $"接続エラー: {_service.LastError}");
            }
        }
        else
        {
            _ = _loggingService.WriteAsync($"[UI] Updating tray icon to normal state. State: {state}");
            _trayIconService.UpdateIcon("squirrel-notifier.ico");
            _trayIconService.UpdateTooltip("Squirrel Notifier");
            _hasShownErrorBalloon = false;
        }
    }

    private void UpdateControls(SubscriptionState state)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            switch (state)
            {
                case SubscriptionState.Running:
                    StartButton.IsEnabled = false;
                    StopButton.IsEnabled = true;
                    RetryButton.IsEnabled = false;
                    break;
                case SubscriptionState.Stopped:
                    StartButton.IsEnabled = true;
                    StopButton.IsEnabled = false;
                    RetryButton.IsEnabled = false;
                    break;
                case SubscriptionState.Starting:
                case SubscriptionState.Stopping:
                    StartButton.IsEnabled = false;
                    StopButton.IsEnabled = false;
                    RetryButton.IsEnabled = false;
                    break;
                case SubscriptionState.Error:
                    StartButton.IsEnabled = true;
                    StopButton.IsEnabled = false;
                    RetryButton.IsEnabled = true;
                    break;
            }
        });
    }

    private void OnLogAppended(object? sender, string line)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            _logEntries.Add(line);
            const int maxEntries = 200;
            if (_logEntries.Count > maxEntries)
            {
                _logEntries.RemoveAt(0);
            }
        });
    }

    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _loggingService.LogDirectory,
                UseShellExecute = true,
            });
        }
        catch
        {
            // ignore
        }
    }

    private void OnSettingChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        SaveCurrentSettings();
    }

    private void OnTimeoutChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInitializing)
        {
            return;
        }

        SaveCurrentSettings();
    }

    private void SaveCurrentSettings()
    {
        try
        {
            string commandPath = CommandPathBox.Text;
            string arguments = ArgumentsBox.Text;
            string gatewayUrl = GatewayUrlBox.Text;
            string resourceUri = ResourceUriBox.Text;
            int timeoutMs = double.IsNaN(TimeoutBox.Value) ? 60000 : (int)TimeoutBox.Value;

            string launcherPath = LauncherPathBox.Text;
            string launcherArguments = LauncherArgumentsBox.Text;
            int launcherTimeoutMs = double.IsNaN(LauncherTimeoutBox.Value) ? 300000 : (int)LauncherTimeoutBox.Value;

            _settingsService.UpdateSettings(
                commandPath,
                arguments,
                gatewayUrl,
                resourceUri,
                timeoutMs,
                launcherPath,
                launcherArguments,
                launcherTimeoutMs);
        }
        catch
        {
            // Ignore validation errors during typing
        }
    }

    private void OnAppWindowClosing(object? sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs e)
    {
        if (_isExitRequested)
        {
            return;
        }

        e.Cancel = true;
        HideWindowToTray();
    }

    private async Task CheckForUpdatesAsync(bool showNoUpdateDialog)
    {
        if (_isCheckingForUpdates)
        {
            return;
        }

        _isCheckingForUpdates = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            AutoUpdateResult result = await _autoUpdateService.CheckForUpdatesAsync(cts.Token);
            if (!result.HasUpdate || string.IsNullOrWhiteSpace(result.ReleaseUrl))
            {
                if (showNoUpdateDialog)
                {
                    var noUpdateDialog = new ContentDialog
                    {
                        Title = "最新バージョンを利用中です",
                        Content = "新しいバージョンは見つかりませんでした。",
                        CloseButtonText = "閉じる",
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = Content.XamlRoot,
                    };

                    await noUpdateDialog.ShowAsync(ContentDialogPlacement.Popup);
                }

                return;
            }

            // スキップされたバージョンの判定（自動チェック時のみスキップを考慮）
            bool isSkipped = !string.IsNullOrEmpty(result.Tag) && result.Tag == _settingsService.Settings.LastSkippedVersion;
            if (isSkipped && !showNoUpdateDialog)
            {
                return;
            }

            var updateDialog = new ContentDialog
            {
                Title = "新しいバージョンがあります",
                Content = $"最新バージョン {result.LatestVersion} がリリースされています。ダウンロードページを開きますか？",
                PrimaryButtonText = "ダウンロード",
                SecondaryButtonText = "このバージョンをスキップ",
                CloseButtonText = "後で",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };

            ContentDialogResult dialogResult = await updateDialog.ShowAsync(ContentDialogPlacement.Popup);
            if (dialogResult == ContentDialogResult.Primary)
            {
                TryOpenReleasePage(result.ReleaseUrl);
            }
            else if (dialogResult == ContentDialogResult.Secondary)
            {
                _settingsService.UpdateLastSkippedVersion(result.Tag ?? result.LatestVersion.ToString());
            }
        }
        catch (Exception ex)
        {
            await _loggingService.WriteAsync($"自動更新チェック中にエラーが発生しました: {ex.Message}").ConfigureAwait(false);
        }
        finally
        {
            _isCheckingForUpdates = false;
        }
    }

    private void TryOpenReleasePage(string releaseUrl)
    {
        if (string.IsNullOrWhiteSpace(releaseUrl))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = releaseUrl,
                UseShellExecute = true,
            });
        }
        catch
        {
            // ignore
        }
    }

    private void OnReviewEventReceived(object? sender, Models.ReviewEvent e)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            _reviewEvents.Insert(0, e);
            const int maxEvents = 20;
            if (_reviewEvents.Count > maxEvents)
            {
                _reviewEvents.RemoveAt(_reviewEvents.Count - 1);
            }
        });
    }

    private void OnOpenPrClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.CommandParameter is string url)
        {
            if (Helpers.UrlValidator.IsSafeGitHubUrl(url))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true,
                    });
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    private void OnLaunchReviewRequested(object? sender, string eventId)
    {
        _ = DispatcherQueue.TryEnqueue(async () =>
        {
            Models.ReviewEvent? targetEvent = null;
            foreach (Models.ReviewEvent ev in _reviewEvents)
            {
                if (ev.EventId == eventId)
                {
                    targetEvent = ev;
                    break;
                }
            }

            if (targetEvent == null)
            {
                await _loggingService.WriteAsync($"Launch request ignored: event with ID {eventId} not found in history.").ConfigureAwait(false);
                return;
            }

            ShowWindowFromTray();
            await ExecuteReviewAsync(targetEvent).ConfigureAwait(false);
        });
    }

    private async void OnLaunchReviewClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.CommandParameter is Models.ReviewEvent reviewEvent)
        {
            await ExecuteReviewAsync(reviewEvent).ConfigureAwait(false);
        }
    }

    private async Task ExecuteReviewAsync(Models.ReviewEvent reviewEvent)
    {
        if (_launcherService.IsRunning)
        {
            ContentDialog dialog = new ContentDialog
            {
                Title = "レビュー実行エラー",
                Content = "別のレビューアクションが既に実行中です。",
                CloseButtonText = "閉じる",
                XamlRoot = Content.XamlRoot,
            };
            await dialog.ShowAsync(ContentDialogPlacement.Popup);
            return;
        }

        try
        {
            using CancellationTokenSource cts = new CancellationTokenSource();

            ProgressRing progressRing = new ProgressRing { IsActive = true, Margin = new Thickness(0, 16, 0, 0) };
            StackPanel panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = $"{reviewEvent.Repository}#{reviewEvent.PrNumber} のレビューを実行しています..." });
            panel.Children.Add(progressRing);

            ContentDialog dialog = new ContentDialog
            {
                Title = "レビュー実行中",
                Content = panel,
                CloseButtonText = "キャンセル",
                XamlRoot = Content.XamlRoot,
            };

            Task<LauncherResult> launchTask = _launcherService.LaunchAsync(reviewEvent, cts.Token);
            IAsyncOperation<ContentDialogResult> dialogTask = dialog.ShowAsync(ContentDialogPlacement.Popup);

            Task completedTask = await Task.WhenAny(launchTask, dialogTask.AsTask()).ConfigureAwait(true);

            if (completedTask == launchTask)
            {
                dialog.Hide();
                LauncherResult result = await launchTask.ConfigureAwait(true);

                StackPanel resultPanel = new StackPanel { Spacing = 8 };
                if (result.Success)
                {
                    resultPanel.Children.Add(new TextBlock { Text = "レビューの実行に成功しました。", FontWeight = Microsoft.UI.Text.FontWeights.Bold });
                }
                else
                {
                    string err = string.IsNullOrEmpty(result.ErrorMessage) ? "レビューが異常終了しました。" : result.ErrorMessage;
                    resultPanel.Children.Add(new TextBlock
                    {
                        Text = $"エラー: {err}",
                        Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red),
                    });
                }

                if (result.ExitCode.HasValue)
                {
                    resultPanel.Children.Add(new TextBlock { Text = $"終了コード: {result.ExitCode}" });
                }

                if (!string.IsNullOrWhiteSpace(result.Stdout))
                {
                    resultPanel.Children.Add(new TextBlock { Text = "標準出力:", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                    resultPanel.Children.Add(new TextBox { Text = result.Stdout, TextWrapping = TextWrapping.Wrap, IsReadOnly = true, MaxHeight = 120, AcceptsReturn = true });
                }

                if (!string.IsNullOrWhiteSpace(result.Stderr))
                {
                    resultPanel.Children.Add(new TextBlock { Text = "標準エラー出力:", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                    resultPanel.Children.Add(new TextBox { Text = result.Stderr, TextWrapping = TextWrapping.Wrap, IsReadOnly = true, MaxHeight = 120, AcceptsReturn = true });
                }

                ContentDialog resultDialog = new ContentDialog
                {
                    Title = result.Success ? "レビュー実行成功" : "レビュー実行失敗",
                    Content = resultPanel,
                    CloseButtonText = "閉じる",
                    XamlRoot = Content.XamlRoot,
                };
                await resultDialog.ShowAsync(ContentDialogPlacement.Popup);
            }
            else
            {
                cts.Cancel();
                _launcherService.Cancel();
                await launchTask.ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            ContentDialog errDialog = new ContentDialog
            {
                Title = "エラー",
                Content = $"レビューの実行中に予期しないエラーが発生しました:\n{ex.Message}",
                CloseButtonText = "閉じる",
                XamlRoot = Content.XamlRoot,
            };
            await errDialog.ShowAsync(ContentDialogPlacement.Popup);
        }
    }

    private async void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (_isAutoStartToggling)
        {
            return;
        }

        _isAutoStartToggling = true;
        try
        {
            if (AutoStartToggle.IsOn)
            {
                string exePath = TaskSchedulerService.GetExePath();
                ContentDialog confirmDialog = new ContentDialog
                {
                    Title = "自動起動を設定します",
                    Content = $"以下の内容でタスクスケジューラへ登録します\n\n　タスク名: Squirrel Notifier\n　実行ファイル: {exePath}\n　引数: --tray\n　トリガー: ログオン時（現在のユーザー）",
                    PrimaryButtonText = "はい",
                    CloseButtonText = "いいえ",
                    XamlRoot = Content.XamlRoot,
                };
                ContentDialogResult confirmed = await confirmDialog.ShowAsync(ContentDialogPlacement.Popup);
                if (confirmed != ContentDialogResult.Primary)
                {
                    AutoStartToggle.IsOn = false;
                    return;
                }

                await _taskSchedulerService.RegisterAsync().ConfigureAwait(true);
            }
            else
            {
                ContentDialog confirmDialog = new ContentDialog
                {
                    Title = "自動起動を解除します",
                    Content = "自動起動タスクを削除します。よろしいですか？",
                    PrimaryButtonText = "はい",
                    CloseButtonText = "いいえ",
                    XamlRoot = Content.XamlRoot,
                };
                ContentDialogResult confirmed = await confirmDialog.ShowAsync(ContentDialogPlacement.Popup);
                if (confirmed != ContentDialogResult.Primary)
                {
                    AutoStartToggle.IsOn = true;
                    return;
                }

                await _taskSchedulerService.UnregisterAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            ContentDialog dialog = new ContentDialog
            {
                Title = "自動起動の設定に失敗しました",
                Content = ex.Message,
                CloseButtonText = "閉じる",
                XamlRoot = Content.XamlRoot,
            };
            await dialog.ShowAsync(ContentDialogPlacement.Popup);
        }
        finally
        {
            _isAutoStartToggling = false;
        }

        await RefreshAutoStartStatusAsync().ConfigureAwait(true);
    }

    private async void OnRepairAutoStartClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _taskSchedulerService.RepairAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ContentDialog dialog = new ContentDialog
            {
                Title = "タスク修復に失敗しました",
                Content = ex.Message,
                CloseButtonText = "閉じる",
                XamlRoot = Content.XamlRoot,
            };
            await dialog.ShowAsync(ContentDialogPlacement.Popup);
        }

        await RefreshAutoStartStatusAsync().ConfigureAwait(true);
    }

    private async Task RefreshAutoStartStatusAsync()
    {
        TaskRegistrationStatus status = await _taskSchedulerService.GetStatusAsync().ConfigureAwait(true);
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            _isAutoStartToggling = true;
            try
            {
                if (status == TaskRegistrationStatus.Registered)
                {
                    AutoStartToggle.IsOn = true;
                    AutoStartStatusText.Text = "登録済み";
                    RepairAutoStartButton.IsEnabled = true;
                    OnboardingInfoBar.IsOpen = false;
                }
                else
                {
                    AutoStartToggle.IsOn = false;
                    AutoStartStatusText.Text = "未登録";
                    RepairAutoStartButton.IsEnabled = false;
                    OnboardingInfoBar.IsOpen = true;
                }
            }
            finally
            {
                _isAutoStartToggling = false;
            }
        });
    }
}
