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
using SquirrelNotifier.WinUI3.Services;
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
    private readonly TrayIconService _trayIconService;
    private TrayContextMenu? _contextMenu;
    private readonly nint _hwnd;
    private readonly bool _isInitializing = true;

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

    internal MainWindow(McpSubscriptionService service, LoggingService loggingService, SettingsService settingsService, AutoUpdateService autoUpdateService, bool showWindow = true)
    {
        InitializeComponent();

        // Set window size (WinUI3 requires this in code)
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new SizeInt32(520, 480));
        appWindow.Closing += OnAppWindowClosing;

        // Set window icon
        SetWindowIcon();

        _service = service;
        _loggingService = loggingService;
        _settingsService = settingsService;
        _autoUpdateService = autoUpdateService;
        _service.StatusTextChanged += OnStatusTextChanged;
        _service.StateChanged += OnStateChanged;
        _loggingService.LogAppended += OnLogAppended;
        LogList.ItemsSource = _logEntries;

        // Load settings
        AppSettings settings = _settingsService.Settings;
        CommandPathBox.Text = settings.SubscriberCommandPath;
        ArgumentsBox.Text = settings.SubscriberArguments;
        GatewayUrlBox.Text = settings.GatewayUrl;
        ResourceUriBox.Text = settings.ResourceUri;
        TimeoutBox.Value = settings.NotificationTimeoutMs;

        _isInitializing = false;

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
            if (state == SubscriptionState.Error && !string.IsNullOrEmpty(_service.LastError))
            {
                StatusText.Text = $"Error: {_service.LastError}";
            }
        });
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

            _settingsService.UpdateSettings(commandPath, arguments, gatewayUrl, resourceUri, timeoutMs);
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
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            AutoUpdateResult result = await _autoUpdateService.CheckForUpdatesAsync(cts.Token).ConfigureAwait(false);
            if (!result.HasUpdate || string.IsNullOrWhiteSpace(result.ReleaseUrl))
            {
                if (showNoUpdateDialog)
                {
                    _ = DispatcherQueue.TryEnqueue(async () =>
                    {
                        var dialog = new ContentDialog
                        {
                            Title = "最新バージョンを利用中です",
                            Content = "新しいバージョンは見つかりませんでした。",
                            CloseButtonText = "閉じる",
                            DefaultButton = ContentDialogButton.Close,
                            XamlRoot = Content.XamlRoot,
                        };

                        await dialog.ShowAsync(ContentDialogPlacement.Popup);
                    });
                }

                return;
            }

            _ = DispatcherQueue.TryEnqueue(async () =>
            {
                var dialog = new ContentDialog
                {
                    Title = "新しいバージョンがあります",
                    Content = $"最新バージョン {result.LatestVersion} がリリースされています。ダウンロードページを開きますか？",
                    PrimaryButtonText = "ダウンロード",
                    CloseButtonText = "後で",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = Content.XamlRoot,
                };

                ContentDialogResult dialogResult = await dialog.ShowAsync(ContentDialogPlacement.Popup);
                if (dialogResult == ContentDialogResult.Primary)
                {
                    TryOpenReleasePage(result.ReleaseUrl);
                }
            });
        }
        catch
        {
            // サイレントに失敗させる
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
}
