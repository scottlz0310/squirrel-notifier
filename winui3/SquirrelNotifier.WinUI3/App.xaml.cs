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
    private readonly McpSubscriptionService _subscriptionService;
    private readonly AutoUpdateService _autoUpdateService;

    public App()
    {
        InitializeComponent();
        AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
        AppNotificationManager.Default.Register();
        _notificationService.Initialize();

        // Create subscription service with settings
        _subscriptionService = new McpSubscriptionService(_settingsService, _notificationService, _loggingService);
        _autoUpdateService = new AutoUpdateService(_loggingService);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Check for command-line arguments
        string[] commandLineArgs = Environment.GetCommandLineArgs();
        bool showWindow = !commandLineArgs.Contains("--tray") && !commandLineArgs.Contains("-t");

        _window = new MainWindow(_subscriptionService, _loggingService, _settingsService, _autoUpdateService, showWindow);
        _window.Closed += OnWindowClosed;

        // Activate window if it should be shown
        if (showWindow)
        {
            _window.Activate();
        }

        _subscriptionService.Start();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _subscriptionService.DisposeAsync().AsTask().ConfigureAwait(false);
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
