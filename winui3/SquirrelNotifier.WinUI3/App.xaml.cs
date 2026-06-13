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
    private readonly KernelWatcherService _watcherService;
    private readonly AutoUpdateService _autoUpdateService;

    public App()
    {
        InitializeComponent();
        AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
        AppNotificationManager.Default.Register();
        _notificationService.Initialize();

        // Create watcher service with settings
        var interval = TimeSpan.FromHours(_settingsService.Settings.CheckIntervalHours);
        _watcherService = new KernelWatcherService(_notificationService, _loggingService, interval);
        _autoUpdateService = new AutoUpdateService(_loggingService);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Check for command-line arguments
        string[] commandLineArgs = Environment.GetCommandLineArgs();
        bool showWindow = !commandLineArgs.Contains("--tray") && !commandLineArgs.Contains("-t");

        _window = new MainWindow(_watcherService, _loggingService, _settingsService, _autoUpdateService, showWindow);
        _window.Closed += OnWindowClosed;

        // Activate window if it should be shown
        if (showWindow)
        {
            _window.Activate();
        }

        _watcherService.Start();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _watcherService.DisposeAsync().AsTask().ConfigureAwait(false);
        _autoUpdateService.Dispose();
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        // Show window when notification is clicked
        if (_window != null)
        {
            _window.DispatcherQueue.TryEnqueue(() => _window.ShowWindowFromTray());
        }
    }
}
