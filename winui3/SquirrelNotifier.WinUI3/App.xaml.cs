// <copyright file="App.xaml.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3;

[ExcludeFromCodeCoverage]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "WinUI entry point requires public accessibility")]
public partial class App : Application
{
    private MainWindow? _window;
    private readonly NotificationService _notificationService = new();
    private readonly LoggingService _loggingService = new();
    private readonly SettingsService _settingsService = new();
    private readonly CacheService? _cacheService;
    private readonly McpSubscriptionService _subscriptionService;
    private readonly AutoUpdateService _autoUpdateService;
    private readonly ReviewLauncherService _launcherService;
    private readonly TaskSchedulerService _taskSchedulerService = new();
    private readonly EnqueueReviewService _enqueueReviewService;

    public App()
    {
        InitializeComponent();

        try
        {
            _cacheService = new CacheService();
        }
        catch (Exception ex)
        {
            _ = _loggingService.WriteAsync($"[WARN] CacheService の初期化に失敗。キャッシュなしで起動します: {ex.Message}");
        }

        bool notificationRegistered = false;
        try
        {
            AppNotificationManager.Default.Register();
            notificationRegistered = true;
        }
        catch (System.Runtime.InteropServices.COMException ex) when (ex.Message.Contains("Insights.Resource.dll", StringComparison.Ordinal))
        {
            // self-contained モードで Insights.Resource.dll が見つからない場合の既知の問題
            _ = _loggingService.WriteAsync($"[WARN] AppNotificationManager.Register() 失敗（{ex.HResult:X8}）: {ex.Message}");
        }

        if (notificationRegistered)
        {
            _notificationService.Initialize();
            _notificationService.OpenAppRequested += OnOpenAppRequested;
        }

        _subscriptionService = new McpSubscriptionService(_settingsService, _notificationService, _loggingService, cacheService: _cacheService);
        _launcherService = new ReviewLauncherService(_settingsService, _loggingService);
        _autoUpdateService = new AutoUpdateService(_loggingService);
        _enqueueReviewService = new EnqueueReviewService(_settingsService, _loggingService);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Check for command-line arguments
        string[] commandLineArgs = Environment.GetCommandLineArgs();
        bool showWindow = !commandLineArgs.Contains("--tray") && !commandLineArgs.Contains("-t");

        _window = new MainWindow(_subscriptionService, _loggingService, _settingsService, _autoUpdateService, _notificationService, _launcherService, _taskSchedulerService, _enqueueReviewService, showWindow);
        _window.Closed += OnWindowClosed;

        _window.Activate();

        if (!showWindow)
        {
            _window.HideWindowToTray();
        }

        Program.Reactivated += OnReactivated;

        _subscriptionService.Start();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        Program.Reactivated -= OnReactivated;
        _notificationService.OpenAppRequested -= OnOpenAppRequested;
        _subscriptionService.DisposeAsync().AsTask().ConfigureAwait(false);
        _autoUpdateService.Dispose();
    }

    private void OnReactivated(object? sender, Microsoft.Windows.AppLifecycle.AppActivationArguments e)
    {
        // 二重起動を検知した別プロセスからリダイレクトされた場合、既存インスタンスのウィンドウを前面化する
        _ = _loggingService.WriteAsync("[INFO] 二重起動を検知。既存インスタンスのウィンドウを前面化します。");
        if (_window != null)
        {
            _window.DispatcherQueue.TryEnqueue(() => _window.ShowWindowFromTray());
        }
    }

    private void OnOpenAppRequested(object? sender, EventArgs e)
    {
        // Show window when notification is clicked
        if (_window != null)
        {
            _window.DispatcherQueue.TryEnqueue(() => _window.ShowWindowFromTray());
        }
    }
}
