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
    private readonly bool _notificationRegistrationFailed;
    private readonly NotificationService _notificationService = new();
    private readonly LoggingService _loggingService = new();
    private readonly SettingsService _settingsService = new();
    private readonly CacheService? _cacheService;
    private readonly McpSubscriptionService _subscriptionService;
    private readonly AutoUpdateService _autoUpdateService;
    private readonly ReviewLauncherService _launcherService;
    private readonly TaskSchedulerService _taskSchedulerService = new();
    private readonly EnqueueReviewService _enqueueReviewService;
    private readonly RateLimitReminderService _rateLimitReminderService;
    private readonly RateLimitFileService _rateLimitFileService;

    public App()
    {
        InitializeComponent();

        UnhandledException += OnApplicationUnhandledException;

        try
        {
            _cacheService = new CacheService();
        }
        catch (Exception ex)
        {
            _ = _loggingService.WriteAsync($"[WARN] CacheService の初期化に失敗。キャッシュなしで起動します: {ex.Message}");
        }

        // 実行中プロセスで通知アクティベーションを受け取るには、NotificationInvoked の
        // 購読を Register() より前に行う必要がある（#130）。
        // https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/notifications/app-notifications/app-notifications-quickstart
        try
        {
            _notificationService.Initialize();
        }
        catch (System.Runtime.InteropServices.COMException ex) when (ex.Message.Contains("Insights.Resource.dll", StringComparison.Ordinal))
        {
            // self-contained モードで Insights.Resource.dll が見つからない場合の既知の問題。
            // Register() と同条件で発生しうるため、ここで捕捉せず起動クラッシュさせない。
            _ = _loggingService.WriteAsync($"[WARN] NotificationService.Initialize() 失敗（{ex.HResult:X8}）: {ex.Message}");
        }

        _notificationService.OpenAppRequested += OnOpenAppRequested;

        try
        {
            AppNotificationManager.Default.Register();
        }
        catch (System.Runtime.InteropServices.COMException ex) when (ex.Message.Contains("Insights.Resource.dll", StringComparison.Ordinal))
        {
            // self-contained モードで Insights.Resource.dll が見つからない場合の既知の問題
            // （#169、microsoft/WindowsAppSDK#6071。Microsoft 側の未解決バグ）。
            // Register() はボタン操作（NotificationInvoked）の受信にのみ必要であり、
            // AppNotificationManager.Show() によるトースト表示自体には影響しない。
            // そのためトースト自体は引き続き表示されるが、ボタン操作に反応しない場合がある旨を
            // MainWindow 側でトレイバルーンと InfoBar により案内する
            _notificationRegistrationFailed = true;
            _ = _loggingService.WriteAsync($"[WARN] AppNotificationManager.Register() 失敗（{ex.HResult:X8}）: {ex.Message}");
        }

        _subscriptionService = new McpSubscriptionService(_settingsService, _notificationService, _loggingService, cacheService: _cacheService);
        _launcherService = new ReviewLauncherService(_settingsService, _loggingService);
        _autoUpdateService = new AutoUpdateService(_loggingService);
        _enqueueReviewService = new EnqueueReviewService(_settingsService, _loggingService);
        _rateLimitReminderService = new RateLimitReminderService(_notificationService);
        _rateLimitFileService = new RateLimitFileService(_settingsService.SettingsDirectory);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Microsoft.Windows.AppLifecycle.AppActivationArguments activationArgs =
            Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
        bool isNotificationActivation =
            activationArgs.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.AppNotification;

        // Check for command-line arguments
        string[] commandLineArgs = Environment.GetCommandLineArgs();
        bool showWindow = isNotificationActivation
            || (!commandLineArgs.Contains("--tray") && !commandLineArgs.Contains("-t"));

        _window = new MainWindow(_subscriptionService, _loggingService, _settingsService, _autoUpdateService, _notificationService, _launcherService, _taskSchedulerService, _enqueueReviewService, _rateLimitReminderService, _rateLimitFileService, showWindow, _notificationRegistrationFailed);
        _window.Closed += OnWindowClosed;

        _window.Activate();

        if (!showWindow)
        {
            _window.HideWindowToTray();
        }

        Program.Reactivated += OnReactivated;

        _subscriptionService.Start();

        // アプリ未起動時に通知ボタンから起動された場合、NotificationInvoked は発火しない
        // ため、起動引数（openUrl / launchReview / openApp）をここで処理する（#130）
        if (isNotificationActivation
            && activationArgs.Data is Microsoft.Windows.AppNotifications.AppNotificationActivatedEventArgs notificationArgs)
        {
            _notificationService.HandleActivation(notificationArgs);
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        Program.Reactivated -= OnReactivated;
        _notificationService.OpenAppRequested -= OnOpenAppRequested;
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

    private void OnOpenAppRequested(object? sender, EventArgs e)
    {
        // Show window when notification is clicked
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
