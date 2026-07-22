// <copyright file="App.xaml.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using Microsoft.UI.Xaml;
using SquirrelNotifier.WinUI3.Helpers;
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
    private readonly ReviewRegistrationService _reviewRegistrationService;
    private readonly RateLimitReminderService _rateLimitReminderService;
    private readonly RateLimitFileService _rateLimitFileService;

    public App()
    {
        InitializeComponent();

        UnhandledException += OnApplicationUnhandledException;

        // トレイの右クリックメニューはネイティブメニューとして描画されるため、
        // XAML のテーマとは別に、プロセス単位でシステムテーマ追従を有効にする（#202）。
        NativeMenuTheme.EnableSystemThemeForPopupMenus();

        try
        {
            _cacheService = new CacheService();
        }
        catch (Exception ex)
        {
            _ = _loggingService.WriteAsync($"[WARN] CacheService の初期化に失敗。キャッシュなしで起動します: {ex.Message}");
        }

        _subscriptionService = new McpSubscriptionService(_settingsService, _notificationService, _loggingService, cacheService: _cacheService);
        _launcherService = new ReviewLauncherService(_settingsService, _loggingService);
        _autoUpdateService = new AutoUpdateService(_loggingService);
        var enqueueReviewService = new EnqueueReviewService(_settingsService, _loggingService);
        _reviewRegistrationService = new ReviewRegistrationService(_subscriptionService, enqueueReviewService);
        _rateLimitReminderService = new RateLimitReminderService(_notificationService);
        _rateLimitFileService = new RateLimitFileService(_settingsService.SettingsDirectory);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Check for command-line arguments
        string[] commandLineArgs = Environment.GetCommandLineArgs();
        bool showWindow = !commandLineArgs.Contains("--tray") && !commandLineArgs.Contains("-t");

        _window = new MainWindow(_subscriptionService, _loggingService, _settingsService, _autoUpdateService, _notificationService, _launcherService, _taskSchedulerService, _reviewRegistrationService, _rateLimitReminderService, _rateLimitFileService, showWindow);
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
        _subscriptionService.DisposeAsync().AsTask().ConfigureAwait(false);
        _autoUpdateService.Dispose();
        _rateLimitReminderService.Dispose();
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

    private void OnApplicationUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // クラッシュ直前のため、原因特定用の証跡を確実に残すべく同期的にログ書き込みを待つ（#174）
        _loggingService.WriteAsync($"[FATAL] UIスレッドで未処理例外が発生しました: {e.Exception.GetType().FullName}: {e.Message}{Environment.NewLine}{e.Exception.StackTrace}").GetAwaiter().GetResult();
    }
}
