// <copyright file="MainWindow.xaml.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    private readonly McpSubscriptionService _service;
    private readonly LoggingService _loggingService;
    private readonly SettingsService _settingsService;
    private readonly UpdateCheckCoordinator _updateCheckCoordinator;
    private readonly LogEntryCollectionCoordinator _logEntryCoordinator = new();
    private readonly ReviewEventCollectionCoordinator _reviewEventCollectionCoordinator = new();
    private readonly TrayIconService _trayIconService;
    private readonly nint _hwnd;
    private readonly bool _isInitializing = true;
    private readonly INotificationService _notificationService;
    private readonly IReviewLauncherService _launcherService;
    private readonly IUrlOpener _urlOpener;
    private readonly IFileOpener _fileOpener;
    private readonly IClipboardService _clipboardService;
    private readonly CopyFeedbackCoordinator _copyFeedbackCoordinator;
    private readonly IWindowIconService _windowIconService;
    private readonly ITaskSchedulerService _taskSchedulerService;
    private readonly ReviewRegistrationCoordinator _reviewRegistrationCoordinator;
    private readonly ReviewEventCleanupCoordinator _reviewEventCleanupCoordinator;
    private readonly IRateLimitReminderService _rateLimitReminderService;
    private readonly RateLimitReminderCoordinator _rateLimitReminderCoordinator;
    private readonly RateLimitAgentMonitoringCoordinator _rateLimitAgentMonitoringCoordinator;
    private readonly RateLimitSnapshotService _rateLimitSnapshotService;
    private readonly AutoPauseGate _autoPauseGate = new();
    private readonly SubscriptionStateCoordinator _subscriptionStateCoordinator = new();
    private readonly ReviewStartCoordinator _reviewStartCoordinator;
    private readonly TrayCommandCoordinator _trayCommandCoordinator;
    private readonly WindowLifecycleCoordinator _windowLifecycleCoordinator;
    private readonly RateLimitRefreshCoordinator _rateLimitRefreshCoordinator;
    private readonly SettingsCoordinator _settingsCoordinator;
    private readonly LauncherPresetCoordinator _launcherPresetCoordinator;
    private readonly AutoStartCoordinator _autoStartCoordinator;
    private readonly GatewayLoginCoordinator _gatewayLoginCoordinator = new();
    private readonly ReviewEventProcessingCoordinator _reviewEventProcessingCoordinator;
    private readonly ObservableCollection<Models.RateLimitInfo> _rateLimits = new();
    private readonly ObservableCollection<Models.RateLimitAgentOption> _rateLimitAgentOptions = new();
    private ScrollViewer? _logListScrollViewer;

    // トレイポップアップのコンテンツ。XAML ではなくコードで生成し TaskbarIcon へ後から代入する（#229）
    private readonly ReviewNotificationPopup _reviewNotificationContent;
    private bool _isTrayPopupAvailable;

    // ライブログウィンドウ（#144）のマネージド参照。保持しないと ExecuteReviewAsync 終了後に
    // Window ラッパーが GC 対象になり、失敗時に診断用として開き続けるべきウィンドウが死ぬ。
    // 同時実行抑止によりウィンドウは常に 1 つのため単一フィールドで足りる
    private AgentExecutionWindow? _agentExecutionWindow;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    private const int _swHide = 0;
    private const int _swShow = 5;

    internal MainWindow(
        McpSubscriptionService service,
        LoggingService loggingService,
        SettingsService settingsService,
        AutoUpdateService autoUpdateService,
        INotificationService notificationService,
        IReviewLauncherService launcherService,
        IUrlOpener urlOpener,
        IFileOpener fileOpener,
        IClipboardService clipboardService,
        IWindowIconService windowIconService,
        ITaskSchedulerService taskSchedulerService,
        ReviewRegistrationService reviewRegistrationService,
        IRateLimitReminderService rateLimitReminderService,
        RateLimitFileService rateLimitFileService,
        ReviewEventCleanupCoordinator reviewEventCleanupCoordinator,
        bool showWindow = true)
    {
        InitializeComponent();

        // Auto-Pause（#147）の状態はセッション終了時（ライブログウィンドウ側の評価）にも
        // 変わるため、イベント経由でメイン UI の表示へ反映する
        _autoPauseGate.StateChanged += OnAutoPauseStateChanged;

        // Set window size (WinUI3 requires this in code)
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new SizeInt32(520, 580));
        appWindow.Closing += OnAppWindowClosing;

        // ウィンドウアイコンを設定
        _windowIconService = windowIconService;
        _ = _windowIconService.TrySetIcon(_hwnd);

        _service = service;
        _loggingService = loggingService;
        _settingsService = settingsService;
        _rateLimitAgentMonitoringCoordinator = new(_settingsService);
        _urlOpener = urlOpener;
        _fileOpener = fileOpener;
        _clipboardService = clipboardService;
        _copyFeedbackCoordinator = new CopyFeedbackCoordinator();
        _copyFeedbackCoordinator.Expired += OnCopyFeedbackExpired;
        _copyFeedbackCoordinator.ExpirationFailed += OnCopyFeedbackExpirationFailed;
        _updateCheckCoordinator = new UpdateCheckCoordinator(
            autoUpdateService.CheckForUpdatesAsync, settingsService, loggingService, _urlOpener);
        _notificationService = notificationService;
        _launcherService = launcherService;
        _taskSchedulerService = taskSchedulerService;
        _reviewRegistrationCoordinator = new(reviewRegistrationService);
        _reviewEventCleanupCoordinator = reviewEventCleanupCoordinator;
        _rateLimitReminderService = rateLimitReminderService;
        _rateLimitReminderCoordinator = new(_rateLimitReminderService);
        _rateLimitSnapshotService = new RateLimitSnapshotService(rateLimitFileService);
        _rateLimitRefreshCoordinator = new RateLimitRefreshCoordinator(
            rateLimitFileService,
            _rateLimitSnapshotService,
            new RateLimitSnapshotResolver(_rateLimitSnapshotService),
            _settingsService,
            _autoPauseGate,
            _rateLimitReminderService);
        _settingsCoordinator = new SettingsCoordinator(_settingsService);
        _launcherPresetCoordinator = new LauncherPresetCoordinator();
        _autoStartCoordinator = new AutoStartCoordinator(_taskSchedulerService);
        _reviewStartCoordinator = new ReviewStartCoordinator(
            _launcherService,
            _settingsService,
            _rateLimitSnapshotService,
            _autoPauseGate,
            _loggingService);
        _reviewEventProcessingCoordinator = new ReviewEventProcessingCoordinator(
            _reviewEventCollectionCoordinator,
            _reviewEventCleanupCoordinator,
            _reviewStartCoordinator.TryStartAutomaticallyAsync);
        _service.StatusTextChanged += OnStatusTextChanged;
        _service.StateChanged += OnStateChanged;
        _loggingService.LogAppended += OnLogAppended;
        _notificationService.ReviewEventReceived += OnReviewEventReceived;
        _reviewEventCleanupCoordinator.EventsRemoved += OnReviewEventsRemoved;
        _notificationService.NotificationRequested += OnNotificationRequested;
        _rateLimitReminderService.ReminderFired += OnRateLimitReminderFired;
        _reviewNotificationContent = new ReviewNotificationPopup();
        _reviewNotificationContent.OpenPrRequested += OnTrayPopupOpenPrRequested;
        _reviewNotificationContent.LaunchReviewRequested += OnTrayPopupLaunchReviewRequested;
        _reviewNotificationContent.OpenAppRequested += OnTrayPopupOpenAppRequested;
        _reviewNotificationContent.DismissRequested += OnTrayPopupDismissRequested;
        LogList.ItemsSource = _logEntryCoordinator.Entries;
        ReviewEventList.ItemsSource = _reviewEventCollectionCoordinator.Events;
        RateLimitList.ItemsSource = _rateLimits;
        RateLimitAgentList.ItemsSource = _rateLimitAgentOptions;
        _reviewEventCleanupCoordinator.Start();

        // Load settings
        AppSettings settings = _settingsService.Settings;
        CommandPathBox.Text = settings.SubscriberCommandPath;
        ArgumentsBox.Text = settings.SubscriberArguments;
        GatewayUrlBox.Text = settings.GatewayUrl;
        ResourceUrisBox.Text = settings.ResourceUris.Count > 0
            ? string.Join("\n", settings.ResourceUris)
            : settings.ResourceUri;
        TimeoutBox.Value = settings.NotificationTimeoutMs;
        ReviewerPathBox.Text = settings.ReviewerLauncherCommandPath;
        ReviewerArgumentsBox.Text = settings.ReviewerLauncherArguments;
        ReviewedPathBox.Text = settings.ReviewedLauncherCommandPath;
        ReviewedArgumentsBox.Text = settings.ReviewedLauncherArguments;
        RepositoryCheckoutMappingsBox.Text = Helpers.RepositoryCheckoutMappingParser.Format(settings.RepositoryCheckoutMappings);
        LauncherTimeoutBox.Value = settings.LauncherTimeoutMs;
        LiveLogAutoCloseToggle.IsOn = settings.LiveLogAutoCloseEnabled;
        AutoReviewStartToggle.IsOn = settings.AutoReviewStartEnabled;

        ReviewerPresetComboBox.ItemsSource = Models.LauncherAgentCatalog.AllWithCustomOption;
        ReviewedPresetComboBox.ItemsSource = Models.LauncherAgentCatalog.AllWithCustomOption;
        UpdateLauncherPresetComboBoxSelection(ReviewerPresetComboBox, settings.ReviewerLauncherPresetId);
        UpdateLauncherPresetComboBoxSelection(ReviewedPresetComboBox, settings.ReviewedLauncherPresetId);

        ReasonComboBox.ItemsSource = ReviewRegistrationCoordinator.Reasons;
        ReasonComboBox.SelectedIndex = 0;

        foreach (Models.RateLimitAgentDefinition definition in Models.RateLimitAgentCatalog.All)
        {
            var option = new Models.RateLimitAgentOption(definition.Id, definition.DisplayName, definition.IsAvailable)
            {
                IsMonitored = definition.IsAvailable && settings.RateLimitMonitoredAgentIds.Contains(definition.Id),
            };
            option.PropertyChanged += OnRateLimitAgentOptionChanged;
            _rateLimitAgentOptions.Add(option);
        }

        _rateLimitAgentMonitoringCoordinator.CompleteInitialization();
        _isInitializing = false;

        UpdateAutoPauseNotApplicableInfoBar();

        // Check auto-start registration status
        _ = RefreshAutoStartStatusAsync();

        _trayIconService = new TrayIconService(TrayIcon);
        TrayIcon.Visibility = Visibility.Visible;

        // TaskbarIcon は自身の Loaded ハンドラでトレイアイコンを生成し、そこで初めて
        // TrayIcon.WindowHandle が確定する。このハンドラは TaskbarIcon のコンストラクタで
        // 登録済みのため後から登録するこちらが後に走る（#229）
        TrayIcon.Loaded += OnTrayIconLoaded;

        _windowLifecycleCoordinator = new(UnsubscribeEventHandlers, DisposeOwnedResources, Close);
        _trayCommandCoordinator = new TrayCommandCoordinator(
            ShowWindowFromTray,
            _service.Start,
            _service.StopAsync,
            () => CheckForUpdatesAsync(showNoUpdateDialog: true),
            _windowLifecycleCoordinator.RequestExit);

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

    private void OnPaneViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        PaneGrid.Height = e.NewSize.Height;
        SettingsScrollViewer.MaxHeight = MainWindowLayout.GetSettingsMaxHeight(e.NewSize.Height);
    }

    private void OnGoToSettingsClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        SettingsExpander.IsExpanded = true;
        _ = DispatcherQueue.TryEnqueue(() => AutoStartToggle.Focus(FocusState.Programmatic));
    }

    public void ShowWindowFromTray()
    {
        ShowWindow(_hwnd, _swShow);
        Activate();
        _ = _reviewEventCleanupCoordinator.RefreshAsync();
    }

    public void HideWindowToTray()
    {
        ShowWindow(_hwnd, _swHide);
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

    private void OnTrayIconLoaded(object sender, RoutedEventArgs e)
    {
        TrayIcon.Loaded -= OnTrayIconLoaded;
        AttachTrayPopup();
    }

    /// <summary>
    /// トレイポップアップのコンテンツを <see cref="TrayIcon"/> へ割り当てる（#229）.
    /// </summary>
    /// <remarks>
    /// H.NotifyIcon 2.5.0 の TrayPopup セッターは同期的に専用 Window（HWND + AppWindow）を生成する。
    /// XAML で代入すると MainWindow の InitializeComponent 内で別 Window を作ることになり、
    /// cold start では E_UNEXPECTED を返して XamlParseException でプロセスが落ちる。XAML ロードの
    /// 外へ出すことで失敗を捕捉でき、トレイアイコン生成後に呼ぶことでポップアップ Window へ
    /// owner window も設定される。
    /// 代入するのは Popup で包まないコンテンツそのものであること。Popup を包むと TrayPopupResolved が
    /// それを採用し、XamlRoot 未設定の Popup を開いて E_UNEXPECTED で落ちる（#199）.
    /// </remarks>
    private void AttachTrayPopup()
    {
        try
        {
            TrayIcon.TrayPopup = _reviewNotificationContent;
            _isTrayPopupAvailable = true;
        }
        catch (Exception ex)
        {
            _isTrayPopupAvailable = false;
            _ = _loggingService.WriteAsync(
                $"[UI] Failed to attach tray popup: {ex.Message}. レビュー通知はバルーン通知で表示します。");
        }
    }

    private void OnTrayOpenCommandExecuteRequested(object sender, ExecuteRequestedEventArgs args)
    {
        ShowWindowFromTray();
    }

    private async void OnTrayRightClickCommandExecuteRequested(object sender, ExecuteRequestedEventArgs args)
    {
        // 表示のたびに現在の購読状態でメニューを組み直す（#202）
        TrayMenuCommand? selected = TrayContextMenu.Show(_hwnd, TrayMenuLayout.Build(_service.State));

        await _trayCommandCoordinator.ExecuteAsync(selected);
    }

    private void OnAutoPauseStateChanged(object? sender, EventArgs e) => UpdateAutoPauseInfoBar();

    private void UnsubscribeEventHandlers()
    {
        _service.StatusTextChanged -= OnStatusTextChanged;
        _service.StateChanged -= OnStateChanged;
        _loggingService.LogAppended -= OnLogAppended;
        _notificationService.ReviewEventReceived -= OnReviewEventReceived;
        _reviewEventCleanupCoordinator.EventsRemoved -= OnReviewEventsRemoved;
        _notificationService.NotificationRequested -= OnNotificationRequested;
        _rateLimitReminderService.ReminderFired -= OnRateLimitReminderFired;
        _reviewNotificationContent.OpenPrRequested -= OnTrayPopupOpenPrRequested;
        _reviewNotificationContent.LaunchReviewRequested -= OnTrayPopupLaunchReviewRequested;
        _reviewNotificationContent.OpenAppRequested -= OnTrayPopupOpenAppRequested;
        _reviewNotificationContent.DismissRequested -= OnTrayPopupDismissRequested;
        _copyFeedbackCoordinator.Expired -= OnCopyFeedbackExpired;
        _copyFeedbackCoordinator.ExpirationFailed -= OnCopyFeedbackExpirationFailed;
        _autoPauseGate.StateChanged -= OnAutoPauseStateChanged;
        TrayIcon.Loaded -= OnTrayIconLoaded;
    }

    private void DisposeOwnedResources()
    {
        _copyFeedbackCoordinator.Dispose();
        _trayIconService.Dispose();
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
            ApplySubscriptionStatePresentation(_subscriptionStateCoordinator.Update(
                state,
                _service.LastError,
                _service.IsAuthenticationRequired));
        });
    }

    private void ApplySubscriptionStatePresentation(SubscriptionStatePresentation presentation)
    {
        _ = _loggingService.WriteAsync(presentation.StateLogMessage);
        if (presentation.NotificationLogMessage is not null)
        {
            _ = _loggingService.WriteAsync(presentation.NotificationLogMessage);
        }

        _trayIconService.UpdateIcon(presentation.IconFileName);
        _trayIconService.UpdateTooltip(presentation.Tooltip);
        if (presentation.Notification is not null)
        {
            _trayIconService.ShowNotification(
                presentation.Notification.Title,
                presentation.Notification.Message,
                presentation.Notification.Icon);
        }

        if (presentation.StatusText is not null)
        {
            StatusText.Text = presentation.StatusText;
        }

        AuthRequiredInfoBar.IsOpen = presentation.IsAuthenticationRequired;
    }

    private void UpdateControls(SubscriptionState state)
    {
        SubscriptionControlAvailability availability = SubscriptionControlAvailability.For(state);

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            StartButton.IsEnabled = availability.CanStart;
            StopButton.IsEnabled = availability.CanStop;
            RetryButton.IsEnabled = availability.CanRetry;
        });
    }

    private void OnLogAppended(object? sender, string line)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            // 追加前のスクロール位置で判定する。追加後は scrollableHeight が伸びて
            // 「末尾にいた」状態が末尾付近でなくなるため（#232）
            bool shouldFollow = ShouldFollowLogTail();

            _logEntryCoordinator.Add(line);

            if (shouldFollow && _logEntryCoordinator.Entries.Count > 0)
            {
                LogList.ScrollIntoView(_logEntryCoordinator.Entries[^1]);
            }
        });
    }

    /// <summary>
    /// Recent activity が新しい行へ自動追従してよいかを判定する。ユーザーが過去ログを読むため
    /// 上へスクロールしている間は追従せず、末尾付近（End キーやスクロールで戻る）に居るときだけ
    /// 追従する（#232）.
    /// </summary>
    private bool ShouldFollowLogTail()
    {
        ScrollViewer? scrollViewer = ResolveLogListScrollViewer();
        if (scrollViewer is null)
        {
            // ScrollViewer をまだ辿れない（初回レイアウト前）。この時点では全行が
            // 表示に収まっているため追従して問題ない
            return true;
        }

        return LogFollowPolicy.ShouldFollow(scrollViewer.VerticalOffset, scrollViewer.ScrollableHeight);
    }

    private ScrollViewer? ResolveLogListScrollViewer()
    {
        if (_logListScrollViewer is not null)
        {
            return _logListScrollViewer;
        }

        _logListScrollViewer = FindDescendantScrollViewer(LogList);
        return _logListScrollViewer;
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }

            ScrollViewer? found = FindDescendantScrollViewer(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        _ = _fileOpener.TryOpen(_loggingService.LogDirectory);
    }

    private void OnSettingChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing || _launcherPresetCoordinator.IsApplying)
        {
            return;
        }

        SaveCurrentSettings();
    }

    private void OnLiveLogAutoCloseToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _settingsService.UpdateLiveLogAutoCloseEnabled(LiveLogAutoCloseToggle.IsOn);
    }

    private void OnAutoReviewStartToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _settingsService.UpdateAutoReviewStartEnabled(AutoReviewStartToggle.IsOn);
    }

    // プリセット選択（#149）: ComboBox で選んだプリセットの command / arguments を
    // テキストボックスへ反映する。反映後の TextChanged から SaveCurrentSettings が呼ばれ、
    // 実際に永続化されるプリセット ID は（選択操作ではなく）その時点のテキスト内容から
    // LauncherAgentCatalog.ResolvePresetId で再判定する。自由編集でプリセットと乖離した場合に
    // 「カスタム」表示へ自然に戻すため.
    private void OnReviewerPresetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _launcherPresetCoordinator.IsSynchronizing)
        {
            return;
        }

        ApplyLauncherPreset(LauncherRole.Reviewer, ReviewerPresetComboBox, ReviewerPathBox, ReviewerArgumentsBox);
    }

    private void OnReviewedPresetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _launcherPresetCoordinator.IsSynchronizing)
        {
            return;
        }

        ApplyLauncherPreset(LauncherRole.Reviewed, ReviewedPresetComboBox, ReviewedPathBox, ReviewedArgumentsBox);
    }

    private void ApplyLauncherPreset(
        LauncherRole role,
        ComboBox comboBox,
        TextBox pathBox,
        TextBox argumentsBox)
    {
        bool applied = _launcherPresetCoordinator.TryApply(
            comboBox.SelectedItem as Models.LauncherAgentDefinition,
            role,
            (command, arguments) =>
            {
                pathBox.Text = command;
                argumentsBox.Text = arguments;
            });
        if (!applied)
        {
            return;
        }

        SaveCurrentSettings();
    }

    // combo box の選択を command / arguments の実値から再判定した presetId へ同期する。
    // SelectionChanged のフィルイン処理を再帰させないよう coordinator の状態で防護する.
    private void UpdateLauncherPresetComboBoxSelection(ComboBox comboBox, string presetId)
    {
        _launcherPresetCoordinator.TrySynchronizeSelection(
            presetId,
            () => comboBox.SelectedItem as Models.LauncherAgentDefinition,
            match => comboBox.SelectedItem = match);
    }

    private async void OnAutoDetectGatewayUrlClick(object sender, RoutedEventArgs e)
    {
        GatewayDetectionResult detection = await _settingsCoordinator.DetectGatewayUrlsAsync(CancellationToken.None);
        if (!detection.Succeeded)
        {
            await ShowAlertDialogAsync(detection.ErrorTitle!, detection.ErrorMessage!);
            return;
        }

        // mcp-gateway は route（例: /mcp/thread-owl）配下に MCP endpoint を割り当てるため、
        // 検出した base URL（host:port）に加えて route パスを選択・入力できるようにする。
        var portCombo = new ComboBox
        {
            ItemsSource = detection.BaseUrls,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var routeBox = new TextBox
        {
            Text = DockerPortParser.DefaultMcpRoute,
            PlaceholderText = DockerPortParser.DefaultMcpRoute,
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "ポート（コンテナ）:" });
        panel.Children.Add(portCombo);
        panel.Children.Add(new TextBlock { Text = "MCP route パス:" });
        panel.Children.Add(routeBox);

        var selectDialog = new ContentDialog
        {
            Title = "Gateway URL を設定",
            Content = panel,
            PrimaryButtonText = "設定",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        ContentDialogResult result = await selectDialog.ShowAsync(ContentDialogPlacement.Popup);
        if (result == ContentDialogResult.Primary && portCombo.SelectedItem is string selectedBase)
        {
            GatewayUrlBox.Text = DockerPortParser.CombineRoute(selectedBase, routeBox.Text);
        }
    }

    private async Task ShowAlertDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync(ContentDialogPlacement.Popup);
    }

    private static readonly string[] _knownResourceUris =
    [
        "queue://review/queue",
        "queue://review/re-review-requests",
    ];

    private async void OnSelectResourceUriClick(object sender, RoutedEventArgs e)
    {
        var listView = new ListView { ItemsSource = _knownResourceUris, SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 160 };
        var selectDialog = new ContentDialog
        {
            Title = "Resource URI を追加",
            Content = listView,
            PrimaryButtonText = "追加",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        ContentDialogResult result = await selectDialog.ShowAsync(ContentDialogPlacement.Popup);
        if (result == ContentDialogResult.Primary && listView.SelectedItems.Count > 0)
        {
            ResourceUrisBox.Text = Helpers.SettingsInputParser.MergeResourceUris(
                ResourceUrisBox.Text,
                listView.SelectedItems.OfType<string>());
        }
    }

    private async void OnFetchResourceUriFromMcpClick(object sender, RoutedEventArgs e)
    {
        ResourceUriFetchResult fetch = await _settingsCoordinator.FetchResourceUrisAsync(
            GatewayUrlBox.Text,
            CancellationToken.None);
        if (!fetch.Succeeded)
        {
            await ShowAlertDialogAsync(fetch.ErrorTitle!, fetch.ErrorMessage!);
            return;
        }

        var listView = new ListView
        {
            ItemsSource = fetch.ResourceUris,
            SelectionMode = ListViewSelectionMode.Multiple,
            MaxHeight = 160,
        };
        var selectDialog = new ContentDialog
        {
            Title = "追加する Resource URI を選択",
            Content = listView,
            PrimaryButtonText = "追加",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        ContentDialogResult result = await selectDialog.ShowAsync(ContentDialogPlacement.Popup);
        if (result == ContentDialogResult.Primary && listView.SelectedItems.Count > 0)
        {
            ResourceUrisBox.Text = Helpers.SettingsInputParser.MergeResourceUris(
                ResourceUrisBox.Text,
                listView.SelectedItems.OfType<string>());
        }
    }

    private async void OnRefreshRateLimitClick(object sender, RoutedEventArgs e)
    {
        RateLimitRefreshResult result = await _rateLimitRefreshCoordinator.RefreshAsync(
            new RateLimitRefreshRequest(_rateLimitAgentOptions, ResourceUrisBox.Text, GatewayUrlBox.Text),
            CancellationToken.None).ConfigureAwait(true);

        foreach (RateLimitRefreshAlert alert in result.Alerts)
        {
            await ShowAlertDialogAsync(alert.Title, alert.Message);
        }

        if (result.Status == RateLimitRefreshStatus.NoTargets)
        {
            return;
        }

        _rateLimits.Clear();
        foreach (Models.RateLimitInfo info in result.Limits)
        {
            _rateLimits.Add(info);
        }

        if (result.LegacySchemaMessage is string legacySchemaMessage)
        {
            LegacySchemaInfoBar.Message = legacySchemaMessage;
        }

        LegacySchemaInfoBar.IsOpen = result.LegacySchemaMessage is not null;
        UpdateAutoPauseInfoBar();
    }

    private void OnRateLimitAgentOptionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => _rateLimitAgentMonitoringCoordinator.Handle(_rateLimitAgentOptions, e);

    private void OnRateLimitReminderFired(object? sender, string reminderKey)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            foreach (Models.RateLimitInfo info in _rateLimits)
            {
                if (info.ReminderKey == reminderKey)
                {
                    info.IsReminderScheduled = false;
                    break;
                }
            }
        });
    }

    private void OnToggleRateLimitReminderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.CommandParameter is not Models.RateLimitInfo info)
        {
            return;
        }

        _rateLimitReminderCoordinator.Toggle(info);
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
        SettingsSaveResult result = _settingsCoordinator.Save(new SettingsInput(
            CommandPathBox.Text,
            ArgumentsBox.Text,
            GatewayUrlBox.Text,
            ResourceUrisBox.Text,
            TimeoutBox.Value,
            ReviewerPathBox.Text,
            ReviewerArgumentsBox.Text,
            ReviewedPathBox.Text,
            ReviewedArgumentsBox.Text,
            LauncherTimeoutBox.Value,
            RepositoryCheckoutMappingsBox.Text));
        if (!result.IsSaved)
        {
            return;
        }

        UpdateLauncherPresetComboBoxSelection(ReviewerPresetComboBox, result.ReviewerPresetId!);
        UpdateLauncherPresetComboBoxSelection(ReviewedPresetComboBox, result.ReviewedPresetId!);
        UpdateAutoPauseNotApplicableInfoBar();
    }

    private void OnAppWindowClosing(object? sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs e)
    {
        if (_windowLifecycleCoordinator.IsExitRequested)
        {
            return;
        }

        e.Cancel = true;
        HideWindowToTray();
    }

    private Task CheckForUpdatesAsync(bool showNoUpdateDialog)
        => _updateCheckCoordinator.CheckAsync(showNoUpdateDialog, ShowUpdateDialogAsync);

    private async Task<UpdateDialogAction> ShowUpdateDialogAsync(UpdateDialogPresentation presentation)
    {
        var dialog = new ContentDialog
        {
            Title = presentation.Title,
            Content = presentation.Message,
            PrimaryButtonText = presentation.PrimaryButtonText,
            SecondaryButtonText = presentation.SecondaryButtonText,
            CloseButtonText = presentation.CloseButtonText,
            DefaultButton = presentation.IsUpdateAvailable ? ContentDialogButton.Primary : ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        return await dialog.ShowAsync(ContentDialogPlacement.Popup) switch
        {
            ContentDialogResult.Primary => UpdateDialogAction.Download,
            ContentDialogResult.Secondary => UpdateDialogAction.Skip,
            _ => UpdateDialogAction.Close,
        };
    }

    private void OnOpenStatuslineDocsClick(object sender, RoutedEventArgs e)
    {
        _ = _urlOpener.TryOpen("https://github.com/scottlz0310/squirrel-notifier/blob/main/docs/statusline-integration.md");
    }

    private void OnReviewEventReceived(object? sender, Models.ReviewEvent e)
    {
        bool enqueued = DispatcherQueue.TryEnqueue(() => HandleReviewEvent(e));

        if (!enqueued)
        {
            throw new InvalidOperationException("レビューイベントを UI スレッドへ配送できませんでした。");
        }
    }

    // UI スレッド上で処理結果を表示へ反映する。保持・終了確認・自動起動の順序は
    // ReviewEventProcessingCoordinator が管理する
    private async void HandleReviewEvent(Models.ReviewEvent reviewEvent)
    {
        try
        {
            ReviewEventProcessingResult processingResult =
                await _reviewEventProcessingCoordinator.ProcessAsync(reviewEvent);
            if (!processingResult.ShouldNotify)
            {
                return;
            }

            ReviewStartResult result = processingResult.StartResult!;
            ShowAgentExecutionWindow(result);
            ShowReviewNotification(reviewEvent, result.IsStarted);
        }
        catch (Exception ex)
        {
            // async void のため例外はここで確実に捕捉する。イベントは一覧に残っているため、
            // 原因を記録したうえでバルーン通知へフォールバックする
            await _loggingService.WriteAsync($"[UI] Failed to handle review event: {ex.Message}");
            ShowReviewBalloon(reviewEvent, isAutoStarted: false);
        }
    }

    private void ShowReviewNotification(Models.ReviewEvent reviewEvent, bool isAutoStarted)
    {
        if (!_isTrayPopupAvailable)
        {
            // ポップアップの生成自体に失敗している（#229）。毎回同じ例外を出すより直接フォールバックする
            ShowReviewBalloon(reviewEvent, isAutoStarted);
            return;
        }

        try
        {
            _reviewNotificationContent.SetReviewEvent(reviewEvent, isAutoStarted);
            _trayIconService.ShowReviewPopup();
        }
        catch (Exception ex)
        {
            // ポップアップ表示の失敗でプロセスを落とさない。イベントは一覧に残っているため、
            // 原因をログへ残したうえでバルーン通知へフォールバックする（#199）。
            _ = _loggingService.WriteAsync($"[UI] Failed to show review popup: {ex.Message}");
            ShowReviewBalloon(reviewEvent, isAutoStarted);
        }
    }

    private void ShowReviewBalloon(Models.ReviewEvent reviewEvent, bool isAutoStarted)
    {
        string message = ReviewNotificationFormatter.BuildSummary(reviewEvent, isAutoStarted);
        _trayIconService.ShowNotification(
            "レビュー通知",
            message,
            H.NotifyIcon.Core.NotificationIcon.Info);
    }

    private void OnNotificationRequested(object? sender, Models.NotificationMessage message)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
            _trayIconService.ShowNotification(message.Title, message.Message));
    }

    private void OnDismissEventClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.CommandParameter is Models.ReviewEvent reviewEvent)
        {
            if (_reviewEventCollectionCoordinator.Remove(reviewEvent))
            {
                _reviewEventCleanupCoordinator.Untrack(reviewEvent.EventId);
            }
        }
    }

    private void OnReviewEventsRemoved(object? sender, ReviewEventsRemovedEventArgs e)
    {
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                foreach (string eventId in e.EventIds)
                {
                    _reviewEventCollectionCoordinator.RemoveByEventId(eventId);
                }
            }))
        {
            _ = _loggingService.WriteAsync("レビューイベントの自動削除結果を UI へ反映できませんでした。");
        }
    }

    private void OnOpenPrClick(object sender, RoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase button && button.CommandParameter is string url)
        {
            if (Helpers.UrlValidator.IsSafeGitHubUrl(url))
            {
                _ = _urlOpener.TryOpen(url);
            }
        }
    }

    private void OnTrayPopupOpenPrRequested(object? sender, Models.ReviewEvent reviewEvent)
    {
        _trayIconService.CloseReviewPopup();
        if (Helpers.UrlValidator.IsSafeGitHubUrl(
            reviewEvent.PrUrl,
            reviewEvent.Repository,
            reviewEvent.PrNumber))
        {
            _ = _urlOpener.TryOpen(reviewEvent.PrUrl);
        }
    }

    private async void OnTrayPopupLaunchReviewRequested(object? sender, Models.ReviewEvent reviewEvent)
    {
        _trayIconService.CloseReviewPopup();
        ShowWindowFromTray();
        await ExecuteReviewAsync(reviewEvent, Models.LauncherRole.Reviewer);
    }

    private void OnTrayPopupOpenAppRequested(object? sender, EventArgs e)
    {
        _trayIconService.CloseReviewPopup();
        ShowWindowFromTray();
    }

    private void OnTrayPopupDismissRequested(object? sender, EventArgs e)
    {
        _trayIconService.CloseReviewPopup();
    }

    private async void OnLaunchReviewerClick(SplitButton sender, SplitButtonClickEventArgs args)
    {
        if (sender.CommandParameter is Models.ReviewEvent reviewEvent)
        {
            await ExecuteReviewAsync(reviewEvent, Models.LauncherRole.Reviewer).ConfigureAwait(false);
        }
    }

    private async void OnLaunchReviewedClick(SplitButton sender, SplitButtonClickEventArgs args)
    {
        if (sender.CommandParameter is Models.ReviewEvent reviewEvent)
        {
            await ExecuteReviewAsync(reviewEvent, Models.LauncherRole.Reviewed).ConfigureAwait(false);
        }
    }

    private void OnCopyReviewerCommandClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.CommandParameter is Models.ReviewEvent reviewEvent)
        {
            CopyLaunchCommand(reviewEvent, Models.LauncherRole.Reviewer);
        }
    }

    private void OnCopyReviewedCommandClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.CommandParameter is Models.ReviewEvent reviewEvent)
        {
            CopyLaunchCommand(reviewEvent, Models.LauncherRole.Reviewed);
        }
    }

    private void CopyLaunchCommand(Models.ReviewEvent reviewEvent, Models.LauncherRole role)
    {
        try
        {
            string commandLine = _launcherService.BuildCommandLine(reviewEvent, role);

            _clipboardService.SetText(commandLine);

            ShowCopyFeedback(_copyFeedbackCoordinator.ShowLaunchCommandCopied());
        }
        catch (Exception ex)
        {
            ShowCopyFeedback(_copyFeedbackCoordinator.ShowFailure(ex));
        }
    }

    private void ShowCopyFeedback(CopyFeedbackPresentation presentation)
    {
        CopyFeedbackInfoBar.Severity = presentation.Severity == CopyFeedbackSeverity.Error
            ? InfoBarSeverity.Error
            : InfoBarSeverity.Success;
        CopyFeedbackInfoBar.Message = presentation.Message;
        CopyFeedbackInfoBar.IsOpen = true;
    }

    private void OnCopyFeedbackExpired(object? sender, EventArgs e)
    {
        if (!DispatcherQueue.TryEnqueue(() => CopyFeedbackInfoBar.IsOpen = false))
        {
            _ = _loggingService.WriteAsync("コピー通知の自動クローズを UI へ配送できませんでした。");
        }
    }

    private void OnCopyFeedbackExpirationFailed(object? sender, CopyFeedbackExpirationFailure failure)
    {
        _ = _loggingService.WriteAsync(failure.Message);
    }

    /// <summary>
    /// 手動操作によるレビューを起動し、ライブログウィンドウを開く。
    /// 起動可否の判断は <see cref="ReviewStartCoordinator"/> が持ち、ここは結果の提示だけを行う（#264）.
    /// </summary>
    /// <param name="reviewEvent">起動対象のレビューイベント.</param>
    /// <param name="role">使用する launcher スロット.</param>
    /// <returns>実際に起動した場合は <see langword="true"/>.</returns>
    private async Task<bool> ExecuteReviewAsync(Models.ReviewEvent reviewEvent, Models.LauncherRole role)
    {
        if (!await _reviewEventCleanupCoordinator.IsActionAllowedAsync(reviewEvent, CancellationToken.None))
        {
            return false;
        }

        ReviewStartResult result = await _reviewStartCoordinator.StartAsync(
            reviewEvent,
            role,
            ReviewStartTrigger.Manual,
            ConfirmAutoPauseOverrideAsync,
            CancellationToken.None);

        switch (result.Status)
        {
            case ReviewStartStatus.Started:
                ShowAgentExecutionWindow(result);
                return true;
            case ReviewStartStatus.SkippedBusy:
                await ShowReviewStartErrorDialogAsync(
                    "レビュー実行エラー",
                    "別のレビューアクションが既に実行中です。");
                return false;
            case ReviewStartStatus.Failed:
                await ShowReviewStartErrorDialogAsync(
                    "エラー",
                    $"レビューの実行中に予期しないエラーが発生しました:\n{result.FailureMessage}");
                return false;
            default:
                // 再入・Auto-Pause 見送り・override のキャンセルはいずれも通知不要。
                // 特に再入でダイアログを出すこと自体が多重表示例外の原因になる（#147 レビュー指摘）
                return false;
        }
    }

    private async Task ShowReviewStartErrorDialogAsync(string title, string message)
    {
        ContentDialog dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "閉じる",
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync(ContentDialogPlacement.Popup);
    }

    /// <summary>
    /// 起動したセッションのライブログウィンドウ（#144）を開く。lifecycle
    /// （成功時自動クローズ・失敗時保持・クローズ時キャンセル）はウィンドウ側の責務.
    /// </summary>
    /// <param name="result">レビュー起動の結果。起動していない場合は何もしない.</param>
    private void ShowAgentExecutionWindow(ReviewStartResult result)
    {
        if (result.Launch is not ReviewStartLaunch launch)
        {
            return;
        }

        var window = new AgentExecutionWindow(
            launch.Session,
            launch.ViewModel,
            launch.RateLimitGaugeViewModel,
            launch.RateLimitSessionMonitor,
            _autoPauseGate,
            _launcherService.Cancel);
        _agentExecutionWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_agentExecutionWindow, window))
            {
                _agentExecutionWindow = null;
            }
        };
        window.Activate();
    }

    // 誤操作で常用されないよう既定ボタンはキャンセル側にする（#147 手動 override の設計論点）
    private async Task<bool> ConfirmAutoPauseOverrideAsync(AutoPausedLimit pausedLimit)
    {
        ContentDialog dialog = new ContentDialog
        {
            Title = "レートリミット Auto-Pause 中",
            Content = $"{pausedLimit.BuildReasonText()}\n\n"
                + "新規エージェント起動を停止しています。fresh なレートリミット情報で使用率 95% 未満を確認すると自動解除されます。\n"
                + "「今回だけ起動を強行」を選ぶと Paused 状態を維持したままこの 1 回のみ起動します。",
            SecondaryButtonText = "今回だけ起動を強行",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        ContentDialogResult result = await dialog.ShowAsync(ContentDialogPlacement.Popup);
        return result == ContentDialogResult.Secondary;
    }

    private void UpdateAutoPauseInfoBar()
    {
        string? message = AutoPauseInfoBarFormatter.BuildPausedMessage(_autoPauseGate.PausedLimits);
        if (message is not null)
        {
            AutoPauseInfoBar.Message = message;
        }

        AutoPauseInfoBar.IsOpen = message is not null;
    }

    private void UpdateAutoPauseNotApplicableInfoBar()
    {
        string? message = AutoPauseInfoBarFormatter.BuildNotApplicableMessage(
            _settingsService.ResolveLauncherRateLimitAgentId(Models.LauncherRole.Reviewer),
            _settingsService.ResolveLauncherRateLimitAgentId(Models.LauncherRole.Reviewed));
        if (message is not null)
        {
            AutoPauseNotApplicableInfoBar.Message = message;
        }

        AutoPauseNotApplicableInfoBar.IsOpen = message is not null;
    }

    private async void OnEnqueueReviewClick(object sender, RoutedEventArgs e)
    {
        EnqueueReviewButton.IsEnabled = false;
        try
        {
            ReviewRegistrationPresentation presentation = await _reviewRegistrationCoordinator.RegisterAsync(
                PrReferenceBox.Text,
                ReasonComboBox.SelectedItem as string,
                ConfirmSubscriptionStartAsync,
                CancellationToken.None).ConfigureAwait(true);

            if (presentation.IsAuthenticationRequired is bool authenticationRequired)
            {
                // 認証エラーはログイン導線（#183）へ誘導する。ダイアログを閉じた後、
                // 認証 InfoBar の「mcp-gateway にログイン」から復旧できる。
                AuthRequiredInfoBar.IsOpen = authenticationRequired;
            }

            if (presentation.DialogTitle is string title)
            {
                await ShowAlertDialogAsync(title, presentation.DialogMessage!).ConfigureAwait(true);
            }

            if (presentation.ClearInput)
            {
                PrReferenceBox.Text = string.Empty;
            }
        }
        finally
        {
            EnqueueReviewButton.IsEnabled = true;
        }
    }

    private async Task<bool> ConfirmSubscriptionStartAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            Title = "購読が停止しています",
            Content = "レビューを登録する前に購読を開始します。",
            PrimaryButtonText = "購読を開始して登録",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };

        ContentDialogResult result = await dialog.ShowAsync(ContentDialogPlacement.Popup);
        return result == ContentDialogResult.Primary;
    }

    private async void OnLoginToGatewayClick(object sender, RoutedEventArgs e)
    {
        await StartGatewayLoginAsync();
    }

    // mcp-gateway の device flow login をアプリ内から開始する（#183）。認証処理自体は
    // mcp-resource-subscriber が担当し、ここでは起動・進行表示・ブラウザ導線・再購読のみ行う。
    private async Task StartGatewayLoginAsync()
    {
        GatewayLoginStartDecision decision = _gatewayLoginCoordinator.TryBeginLogin(GatewayUrlBox.Text);
        if (!decision.CanStart)
        {
            if (decision.ErrorTitle is string errorTitle)
            {
                await ShowAlertDialogAsync(errorTitle, decision.ErrorMessage!);
            }

            return;
        }

        GatewayLoginButton.IsEnabled = false;

        var loginService = new McpLoginService(_settingsService, _loggingService);
        using var cts = new CancellationTokenSource();

        var statusText = new TextBlock { Text = "認証を開始しています...", TextWrapping = TextWrapping.Wrap };
        var urlValue = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var urlCopyButton = new Button { Content = "URL をコピー", Visibility = Visibility.Collapsed };
        var codeValue = new TextBox { IsReadOnly = true, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Visibility = Visibility.Collapsed };
        var codeCopyButton = new Button { Content = "コードをコピー", Visibility = Visibility.Collapsed };

        // 値が届く前にラベルだけが並ぶと何を待っているのか分からないため、値と同時に表示する
        var urlLabel = new TextBlock { Text = "承認 URL:", FontSize = 12, Visibility = Visibility.Collapsed };
        var codeLabel = new TextBlock { Text = "認証コード:", FontSize = 12, Visibility = Visibility.Collapsed };

        var panel = new StackPanel { Spacing = 8, MinWidth = 360 };
        panel.Children.Add(statusText);
        panel.Children.Add(urlLabel);
        panel.Children.Add(urlValue);
        panel.Children.Add(urlCopyButton);
        panel.Children.Add(codeLabel);
        panel.Children.Add(codeValue);
        panel.Children.Add(codeCopyButton);

        DeviceVerificationView? latestView = null;

        urlCopyButton.Click += (_, _) =>
        {
            if (latestView != null)
            {
                CopyToClipboard(latestView.Url);
            }
        };
        codeCopyButton.Click += (_, _) =>
        {
            if (latestView?.UserCode is string userCode)
            {
                CopyToClipboard(userCode);
            }
        };

        void OnStatus(object? sender, string message)
        {
            _ = DispatcherQueue.TryEnqueue(() => statusText.Text = message);
        }

        void OnVerification(object? sender, DeviceVerificationInfo info)
        {
            DeviceVerificationView view = GatewayLoginCoordinator.DescribeVerification(info);
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                latestView = view;
                urlValue.Text = view.Url;
                urlLabel.Visibility = Visibility.Visible;
                urlValue.Visibility = Visibility.Visible;
                urlCopyButton.Visibility = Visibility.Visible;
                if (view.UserCode is string userCode)
                {
                    codeValue.Text = userCode;
                    codeLabel.Visibility = Visibility.Visible;
                    codeValue.Visibility = Visibility.Visible;
                    codeCopyButton.Visibility = Visibility.Visible;
                }
            });
        }

        loginService.StatusChanged += OnStatus;
        loginService.VerificationReceived += OnVerification;

        var dialog = new ContentDialog
        {
            Title = "mcp-gateway にログイン",
            Content = panel,
            CloseButtonText = "キャンセル",
            XamlRoot = Content.XamlRoot,
        };

        // ShowAsync を呼んでもダイアログは即座に開き終わらない。開く途中で Hide() が到達すると
        // 無視され「認証を開始しています...」のまま残るため、Opened を待ってから閉じる（#200）。
        var closeGate = new Helpers.DeferredDialogCloseGate();
        dialog.Opened += (_, _) =>
        {
            if (closeGate.MarkOpened())
            {
                _ = _loggingService.WriteAsync(
                    "[UI] ログインダイアログの Opened 後に保留中のクローズ要求を実行します。");
                dialog.Hide();
            }
        };

        IAsyncOperation<ContentDialogResult> showOperation = dialog.ShowAsync(ContentDialogPlacement.Popup);

        Task<Models.McpLoginResult> loginTask = loginService.LoginAsync(cts.Token);

        // 認証完了時にダイアログを自動で閉じ、ShowAsync を終了させる
        _ = loginTask.ContinueWith(
            _ =>
            {
                bool enqueued = DispatcherQueue.TryEnqueue(() =>
                {
                    if (closeGate.RequestClose())
                    {
                        dialog.Hide();
                    }
                });

                if (!enqueued)
                {
                    _ = _loggingService.WriteAsync(
                        "[UI] ログインダイアログのクローズ要求を UI スレッドへ配送できませんでした。");
                }
            },
            TaskScheduler.Default);

        try
        {
            await showOperation;

            // ShowAsync がユーザー操作（キャンセル）で戻った場合は login を中断する。
            // 認証完了で dialog.Hide() から戻った場合は既に完了しているため cancel は無害。
            if (!loginTask.IsCompleted)
            {
                cts.Cancel();
            }

            Models.McpLoginResult result = await loginTask.ConfigureAwait(true);
            await HandleLoginResultAsync(result).ConfigureAwait(true);
        }
        finally
        {
            loginService.StatusChanged -= OnStatus;
            loginService.VerificationReceived -= OnVerification;
            _gatewayLoginCoordinator.EndLogin();
            GatewayLoginButton.IsEnabled = true;
        }
    }

    private async Task HandleLoginResultAsync(Models.McpLoginResult result)
    {
        GatewayLoginPresentation presentation = GatewayLoginCoordinator.DescribeResult(result, _service.State);

        if (presentation.CloseAuthRequiredInfoBar)
        {
            AuthRequiredInfoBar.IsOpen = false;
        }

        if (presentation.RestartSubscription)
        {
            _service.Start();
        }

        if (presentation.DialogTitle is string title)
        {
            await ShowAlertDialogAsync(title, presentation.DialogMessage!);
        }
    }

    private void CopyToClipboard(string text)
    {
        try
        {
            _clipboardService.SetText(text);
            ShowCopyFeedback(_copyFeedbackCoordinator.ShowTextCopied());
        }
        catch (Exception ex)
        {
            ShowCopyFeedback(_copyFeedbackCoordinator.ShowFailure(ex));
        }
    }

    private async void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (_autoStartCoordinator.IsToggleSuppressed)
        {
            return;
        }

        AutoStartOperationResult result = await _autoStartCoordinator.ToggleAsync(
            AutoStartToggle.IsOn,
            ShowAutoStartConfirmationAsync);
        if (result.ToggleIsOn is bool toggleIsOn)
        {
            _autoStartCoordinator.ApplyUiState(() => AutoStartToggle.IsOn = toggleIsOn);
        }

        if (result.ErrorTitle is string errorTitle)
        {
            await ShowAlertDialogAsync(errorTitle, result.ErrorMessage!);
        }

        if (result.ShouldRefresh)
        {
            await RefreshAutoStartStatusAsync();
        }
    }

    private async Task<bool> ShowAutoStartConfirmationAsync(AutoStartConfirmation confirmation)
    {
        var dialog = new ContentDialog
        {
            Title = confirmation.Title,
            Content = confirmation.Message,
            PrimaryButtonText = "はい",
            CloseButtonText = "いいえ",
            XamlRoot = Content.XamlRoot,
        };
        ContentDialogResult result = await dialog.ShowAsync(ContentDialogPlacement.Popup);
        return result == ContentDialogResult.Primary;
    }

    private async void OnRepairAutoStartClick(object sender, RoutedEventArgs e)
    {
        if (_autoStartCoordinator.IsToggleSuppressed)
        {
            return;
        }

        AutoStartOperationResult result = await _autoStartCoordinator.RepairAsync();
        if (result.ErrorTitle is string errorTitle)
        {
            await ShowAlertDialogAsync(errorTitle, result.ErrorMessage!);
        }

        if (result.ShouldRefresh)
        {
            await RefreshAutoStartStatusAsync();
        }
    }

    private Task RefreshAutoStartStatusAsync()
        => _autoStartCoordinator.RefreshStatusAsync(ApplyAutoStartStatus);

    private void ApplyAutoStartStatus(AutoStartStatusPresentation presentation)
    {
        if (presentation.ToggleIsOn is bool toggleIsOn)
        {
            AutoStartToggle.IsOn = toggleIsOn;
        }

        AutoStartStatusText.Text = presentation.StatusText;
        RepairAutoStartButton.IsEnabled = presentation.IsRepairEnabled;
        OnboardingInfoBar.IsOpen = presentation.IsOnboardingOpen;
    }
}
