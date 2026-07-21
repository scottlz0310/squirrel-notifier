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
using Microsoft.UI.Xaml.Input;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;
using Windows.ApplicationModel.DataTransfer;
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
    private readonly nint _hwnd;
    private readonly bool _isInitializing = true;
    private readonly INotificationService _notificationService;
    private readonly IReviewLauncherService _launcherService;
    private readonly ITaskSchedulerService _taskSchedulerService;
    private readonly EnqueueReviewService _enqueueReviewService;
    private readonly IRateLimitReminderService _rateLimitReminderService;
    private readonly RateLimitFileService _rateLimitFileService;
    private readonly RateLimitSnapshotService _rateLimitSnapshotService;
    private readonly RateLimitSnapshotResolver _rateLimitSnapshotResolver;
    private readonly AutoPauseGate _autoPauseGate = new();
    private readonly ObservableCollection<Models.RateLimitInfo> _rateLimits = new();
    private readonly ObservableCollection<Models.RateLimitAgentOption> _rateLimitAgentOptions = new();
    private bool _isCheckingForUpdates;
    private bool _hasShownErrorBalloon;
    private bool _isAutoStartToggling;
    private bool _isSyncingLauncherPresetSelection;
    private bool _isApplyingLauncherPreset;
    private bool _isReviewStartPending;
    private bool _isLoginPending;

    // ライブログウィンドウ（#144）のマネージド参照。保持しないと ExecuteReviewAsync 終了後に
    // Window ラッパーが GC 対象になり、失敗時に診断用として開き続けるべきウィンドウが死ぬ。
    // 同時実行抑止によりウィンドウは常に 1 つのため単一フィールドで足りる
    private AgentExecutionWindow? _agentExecutionWindow;
    private CancellationTokenSource? _copyFeedbackCts;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadImage(nint hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    private const int _swHide = 0;
    private const int _swShow = 5;
    private const uint _wmSetIcon = 0x0080;
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
        EnqueueReviewService enqueueReviewService,
        IRateLimitReminderService rateLimitReminderService,
        RateLimitFileService rateLimitFileService,
        bool showWindow = true)
    {
        InitializeComponent();

        // Auto-Pause（#147）の状態はセッション終了時（ライブログウィンドウ側の評価）にも
        // 変わるため、イベント経由でメイン UI の表示へ反映する
        _autoPauseGate.StateChanged += (_, _) => UpdateAutoPauseInfoBar();

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
        _enqueueReviewService = enqueueReviewService;
        _rateLimitReminderService = rateLimitReminderService;
        _rateLimitFileService = rateLimitFileService;
        _rateLimitSnapshotService = new RateLimitSnapshotService(rateLimitFileService);
        _rateLimitSnapshotResolver = new RateLimitSnapshotResolver(_rateLimitSnapshotService);
        _service.StatusTextChanged += OnStatusTextChanged;
        _service.StateChanged += OnStateChanged;
        _loggingService.LogAppended += OnLogAppended;
        _notificationService.ReviewEventReceived += OnReviewEventReceived;
        _notificationService.NotificationRequested += OnNotificationRequested;
        _rateLimitReminderService.ReminderFired += OnRateLimitReminderFired;
        ReviewNotificationContent.OpenPrRequested += OnTrayPopupOpenPrRequested;
        ReviewNotificationContent.LaunchReviewRequested += OnTrayPopupLaunchReviewRequested;
        ReviewNotificationContent.OpenAppRequested += OnTrayPopupOpenAppRequested;
        ReviewNotificationContent.DismissRequested += OnTrayPopupDismissRequested;
        LogList.ItemsSource = _logEntries;
        ReviewEventList.ItemsSource = _reviewEvents;
        RateLimitList.ItemsSource = _rateLimits;
        RateLimitAgentList.ItemsSource = _rateLimitAgentOptions;

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

        ReviewerPresetComboBox.ItemsSource = Models.LauncherAgentCatalog.AllWithCustomOption;
        ReviewedPresetComboBox.ItemsSource = Models.LauncherAgentCatalog.AllWithCustomOption;
        UpdateLauncherPresetComboBoxSelection(ReviewerPresetComboBox, settings.ReviewerLauncherPresetId);
        UpdateLauncherPresetComboBoxSelection(ReviewedPresetComboBox, settings.ReviewedLauncherPresetId);

        ReasonComboBox.ItemsSource = _enqueueReviewReasons;
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

        _isInitializing = false;

        // Check auto-start registration status
        _ = RefreshAutoStartStatusAsync();

        _trayIconService = new TrayIconService(TrayIcon);
        TrayIcon.Visibility = Visibility.Visible;

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

    private void OnTrayOpenCommandExecuteRequested(object sender, ExecuteRequestedEventArgs args)
    {
        ShowWindowFromTray();
    }

    private async void OnTrayRightClickCommandExecuteRequested(object sender, ExecuteRequestedEventArgs args)
    {
        // 表示のたびに現在の購読状態でメニューを組み直す（#202）
        TrayMenuCommand? selected = TrayContextMenu.Show(_hwnd, TrayMenuLayout.Build(_service.State));

        switch (selected)
        {
            case TrayMenuCommand.Open:
                ShowWindowFromTray();
                break;
            case TrayMenuCommand.Start:
                _service.Start();
                break;
            case TrayMenuCommand.Stop:
                await _service.StopAsync();
                break;
            case TrayMenuCommand.CheckForUpdates:
                await CheckForUpdatesAsync(showNoUpdateDialog: true);
                break;
            case TrayMenuCommand.Exit:
                ExitApplication();
                break;
            case null:
            default:
                break;
        }
    }

    private void ExitApplication()
    {
        _isExitRequested = true;
        _service.StatusTextChanged -= OnStatusTextChanged;
        _service.StateChanged -= OnStateChanged;
        _loggingService.LogAppended -= OnLogAppended;
        _notificationService.ReviewEventReceived -= OnReviewEventReceived;
        _notificationService.NotificationRequested -= OnNotificationRequested;
        ReviewNotificationContent.OpenPrRequested -= OnTrayPopupOpenPrRequested;
        ReviewNotificationContent.LaunchReviewRequested -= OnTrayPopupLaunchReviewRequested;
        ReviewNotificationContent.OpenAppRequested -= OnTrayPopupOpenAppRequested;
        ReviewNotificationContent.DismissRequested -= OnTrayPopupDismissRequested;
        _trayIconService?.Dispose();
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

            // 認証が必要な Error になったら、アプリ内ログイン導線（#183）を提示する。
            // 認証以外のエラーや回復時は閉じる。
            AuthRequiredInfoBar.IsOpen = state == SubscriptionState.Error && _service.IsAuthenticationRequired;
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
                if (_service.IsAuthenticationRequired)
                {
                    _ = _loggingService.WriteAsync("[UI] Showing authentication required balloon notification.");
                    _trayIconService.ShowNotification("Squirrel Notifier", _service.LastError, H.NotifyIcon.Core.NotificationIcon.Error);
                }
                else
                {
                    _ = _loggingService.WriteAsync("[UI] Showing connection error balloon notification.");
                    _trayIconService.ShowNotification("Squirrel Notifier", $"接続エラー: {_service.LastError}", H.NotifyIcon.Core.NotificationIcon.Error);
                }
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
        if (_isInitializing || _isApplyingLauncherPreset)
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

    // プリセット選択（#149）: ComboBox で選んだプリセットの command / arguments を
    // テキストボックスへ反映する。反映後の TextChanged から SaveCurrentSettings が呼ばれ、
    // 実際に永続化されるプリセット ID は（選択操作ではなく）その時点のテキスト内容から
    // LauncherAgentCatalog.ResolvePresetId で再判定する。自由編集でプリセットと乖離した場合に
    // 「カスタム」表示へ自然に戻すため.
    private void OnReviewerPresetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _isSyncingLauncherPresetSelection)
        {
            return;
        }

        ApplyLauncherPreset(ReviewerPresetComboBox, ReviewerPathBox, ReviewerArgumentsBox, static d => d.ReviewerArgumentsTemplate);
    }

    private void OnReviewedPresetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _isSyncingLauncherPresetSelection)
        {
            return;
        }

        ApplyLauncherPreset(ReviewedPresetComboBox, ReviewedPathBox, ReviewedArgumentsBox, static d => d.ReviewedArgumentsTemplate);
    }

    private void ApplyLauncherPreset(ComboBox comboBox, TextBox pathBox, TextBox argumentsBox, Func<Models.LauncherAgentDefinition, string> argumentsTemplateSelector)
    {
        if (comboBox.SelectedItem is not Models.LauncherAgentDefinition selected
            || selected.Id == Models.LauncherAgentCatalog.CustomPresetId)
        {
            return;
        }

        // path / arguments の反映中は TextChanged 経由の SaveCurrentSettings を抑止し、
        // 両方反映し終えた後で一度だけ再判定・保存する。反映中に保存すると、arguments が
        // たまたま反映先プリセットの値と一致している場合に arguments 側の TextChanged が
        // 発火せず（値が変わらないため）、path のみ変更された中間状態（= custom 判定）の
        // まま presetId が固定されてしまう（#149 レビュー対応）.
        _isApplyingLauncherPreset = true;
        try
        {
            pathBox.Text = selected.Command;
            argumentsBox.Text = argumentsTemplateSelector(selected);
        }
        finally
        {
            _isApplyingLauncherPreset = false;
        }

        SaveCurrentSettings();
    }

    // combo box の選択を command / arguments の実値から再判定した presetId へ同期する。
    // SelectionChanged のフィルイン処理を再帰させないよう _isSyncingLauncherPresetSelection で防護する.
    private void UpdateLauncherPresetComboBoxSelection(ComboBox comboBox, string presetId)
    {
        Models.LauncherAgentDefinition? match = Models.LauncherAgentCatalog.AllWithCustomOption
            .FirstOrDefault(d => d.Id == presetId);

        if (Equals(comboBox.SelectedItem, match))
        {
            return;
        }

        _isSyncingLauncherPresetSelection = true;
        try
        {
            comboBox.SelectedItem = match;
        }
        finally
        {
            _isSyncingLauncherPresetSelection = false;
        }
    }

    private async void OnAutoDetectGatewayUrlClick(object sender, RoutedEventArgs e)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            process.StartInfo.ArgumentList.Add("ps");
            process.StartInfo.ArgumentList.Add("--filter");
            process.StartInfo.ArgumentList.Add("name=mcp-gateway");
            process.StartInfo.ArgumentList.Add("--format");
            process.StartInfo.ArgumentList.Add("{{.Ports}}");
            process.Start();
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string output = await stdoutTask;
            string stderr = await stderrTask;
            if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
            {
                await ShowAlertDialogAsync("Docker エラー", $"Docker コマンドが失敗しました。\n{stderr.Trim()}");
                return;
            }

            IReadOnlyList<string> baseUrls = DockerPortParser.ParseGatewayBaseUrls(output);
            if (baseUrls.Count == 0)
            {
                await ShowAlertDialogAsync(
                    "コンテナが見つかりませんでした",
                    "コンテナ名に 'mcp-gateway' が含まれているか、コンテナが起動しているか確認してください。");
                return;
            }

            // mcp-gateway は route（例: /mcp/thread-owl）配下に MCP endpoint を割り当てるため、
            // 検出した base URL（host:port）に加えて route パスを選択・入力できるようにする。
            var portCombo = new ComboBox
            {
                ItemsSource = baseUrls,
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
        catch (System.ComponentModel.Win32Exception)
        {
            await ShowAlertDialogAsync(
                "Docker が見つかりませんでした",
                "Docker がインストールされていないか PATH に含まれていません。Gateway URL を手動で入力してください。");
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

    private static readonly string[] _enqueueReviewReasons =
    [
        "opened",
        "synchronized",
        "re-review-requested",
    ];

    private static readonly char[] _resourceUriLineSeparators = ['\r', '\n'];

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
            HashSet<string> existing = ResourceUrisBox.Text
                .Split(_resourceUriLineSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet();
            foreach (object item in listView.SelectedItems)
            {
                if (item is string uri)
                {
                    existing.Add(uri);
                }
            }

            ResourceUrisBox.Text = string.Join("\n", existing);
        }
    }

    private async void OnFetchResourceUriFromMcpClick(object sender, RoutedEventArgs e)
    {
        string gatewayUrl = GatewayUrlBox.Text;
        if (!Uri.TryCreate(gatewayUrl, UriKind.Absolute, out Uri? endpoint))
        {
            await ShowAlertDialogAsync("設定エラー", "Gateway URL が正しくありません。先に Gateway URL を設定してください。");
            return;
        }

        string? token = Environment.GetEnvironmentVariable("MCP_PROBE_AUTH_TOKEN");

        try
        {
            var probe = new Services.McpResourceProbe();
            IReadOnlyList<string> uris = await probe.FetchResourceUrisAsync(endpoint, token, CancellationToken.None);

            if (uris.Count == 0)
            {
                await ShowAlertDialogAsync("リソースが見つかりません", "mcp-gateway からリソース URI を取得しましたが、リストが空でした。");
                return;
            }

            var listView = new ListView { ItemsSource = uris, SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 160 };
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
                HashSet<string> existing = ResourceUrisBox.Text
                    .Split(_resourceUriLineSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet();
                foreach (object item in listView.SelectedItems)
                {
                    if (item is string selectedUri)
                    {
                        existing.Add(selectedUri);
                    }
                }

                ResourceUrisBox.Text = string.Join("\n", existing);
            }
        }
        catch (Exception ex)
        {
            await ShowAlertDialogAsync("取得エラー", Services.McpResourceProbe.GetUserMessage(ex));
        }
    }

    private async void OnRefreshRateLimitClick(object sender, RoutedEventArgs e)
    {
        var fetchedLimits = new List<Models.RateLimitInfo>();

        // Auto-Pause gate（#147/#167）の再評価に使う snapshot。表示取得と同じ操作内で
        // 取得したものを再利用し、gate 用に同じ agent を二重取得しない（#167 レビュー対応）。
        var capturedSnapshots = new Dictionary<string, Models.RateLimitSnapshot>(StringComparer.Ordinal);

        // 旧形式（schemaVersion 等を欠く resetAt-only）の snapshot を書き出しているエージェント（#168）。
        // 一覧表示はできるため気づかれにくいが、Auto-Pause gate の判定対象からは silent に外れる。
        var legacySchemaAgentNames = new List<string>();

        // 1. ローカルファイル経由（statusline フック連携、#139）または App Server 経由（codex、#163）
        // IsAvailable=false は settings.json の手動編集等で IsMonitored=true に
        // なっていても読み取りの対象にしない。
        List<Models.RateLimitAgentOption> monitoredAgents = _rateLimitAgentOptions.Where(o => o.IsMonitored && o.IsAvailable).ToList();
        foreach (Models.RateLimitAgentOption agent in monitoredAgents)
        {
            try
            {
                if (agent.Id == RateLimitSnapshotService.CodexAgentId)
                {
                    // codex は statusline を持たないため App Server（account/rateLimits/read）から取得する
                    (Models.RateLimitSnapshot? snapshot, Services.CodexRateLimitFailureReason? failureReason) =
                        await _rateLimitSnapshotService.CaptureCodexWithFailureReasonAsync(agent.Id, CancellationToken.None).ConfigureAwait(true);
                    if (snapshot == null)
                    {
                        await ShowAlertDialogAsync(
                            "レートリミット情報を取得できません",
                            BuildCodexFailureMessage(agent.DisplayName, failureReason));
                        continue;
                    }

                    capturedSnapshots[agent.Id] = snapshot;

                    string sourceUri = Services.RateLimitFileService.BuildSourceIdentifier(agent.Id);
                    foreach (Models.RateLimitInfo info in snapshot.Limits)
                    {
                        info.SourceUri = sourceUri;
                        fetchedLimits.Add(info);
                    }

                    continue;
                }

                string? json = await _rateLimitFileService.ReadAgentStatusAsync(agent.Id, CancellationToken.None).ConfigureAwait(true);
                if (json == null)
                {
                    await ShowAlertDialogAsync(
                        "レートリミット情報がありません",
                        $"{agent.DisplayName} のレートリミット情報がまだありません。statusline スクリプトの拡張が必要です。詳細は docs/statusline-integration.md を参照してください。");
                    continue;
                }

                Models.RateLimitSnapshot? parsedSnapshot = Services.RateLimitStatusParser.ParseSnapshot(json);
                if (parsedSnapshot is not null)
                {
                    capturedSnapshots[agent.Id] = parsedSnapshot;
                }
                else if (Services.RateLimitStatusParser.IsLegacySchema(json))
                {
                    legacySchemaAgentNames.Add(agent.DisplayName);
                }

                fetchedLimits.AddRange(Services.RateLimitStatusParser.Parse(json, Services.RateLimitFileService.BuildSourceIdentifier(agent.Id)));
            }
            catch (Exception ex)
            {
                await ShowAlertDialogAsync("取得エラー", $"{agent.DisplayName} のレートリミット状態の読み取りに失敗しました: {ex.Message}");
            }
        }

        // 2. MCP ratelimit:// 経由（既存。サーバー側で将来対応された場合のために維持）
        List<string> rateLimitUris = ResourceUrisBox.Text
            .Split(_resourceUriLineSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(uri => uri.StartsWith(Services.RateLimitStatusParser.UriScheme, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // MCP 側の取得に失敗しても、既にローカルファイル経由で取得済みの結果は破棄せず
        // 部分成功として表示する（#139 レビュー対応）。
        if (rateLimitUris.Count > 0)
        {
            string gatewayUrl = GatewayUrlBox.Text;
            if (!Uri.TryCreate(gatewayUrl, UriKind.Absolute, out Uri? endpoint))
            {
                await ShowAlertDialogAsync("設定エラー", "Gateway URL が正しくありません。先に Gateway URL を設定してください。");
            }
            else
            {
                string? token = Environment.GetEnvironmentVariable("MCP_PROBE_AUTH_TOKEN");
                var probe = new Services.McpResourceProbe();

                foreach (string uri in rateLimitUris)
                {
                    try
                    {
                        string json = await probe.ReadResourceTextAsync(endpoint, token, uri, CancellationToken.None).ConfigureAwait(true);
                        fetchedLimits.AddRange(Services.RateLimitStatusParser.Parse(json, uri));
                    }
                    catch (Exception ex)
                    {
                        await ShowAlertDialogAsync("取得エラー", Services.McpResourceProbe.GetUserMessage(ex));
                    }
                }
            }
        }

        if (monitoredAgents.Count == 0 && rateLimitUris.Count == 0)
        {
            await ShowAlertDialogAsync(
                "監視対象未設定",
                "レートリミット監視対象のエージェントが選択されていないか、Resource URIs に ratelimit:// で始まる URI が設定されていません。");
            return;
        }

        _rateLimits.Clear();
        foreach (Models.RateLimitInfo info in fetchedLimits)
        {
            info.IsReminderScheduled = _rateLimitReminderService.IsScheduled(info.ReminderKey);
            _rateLimits.Add(info);
        }

        UpdateLegacySchemaInfoBar(legacySchemaAgentNames);
        await RefreshAutoPauseGateAsync(capturedSnapshots).ConfigureAwait(true);
    }

    // codex の取得不可理由を原因ごとに出し分ける（#174）。JSON-RPC error の code/message は
    // codex CLI バージョンにより変わりうるため確実に判別できず、Unknown を「未ログインの可能性を
    // 含む」表現に留め、断定しない。
    private static string BuildCodexFailureMessage(string agentDisplayName, Services.CodexRateLimitFailureReason? failureReason)
    {
        return failureReason switch
        {
            Services.CodexRateLimitFailureReason.CommandNotFound =>
                $"{agentDisplayName} の codex コマンドが見つかりませんでした。codex CLI がインストールされ、PATH が通っているか確認してください。",
            Services.CodexRateLimitFailureReason.Timeout =>
                $"{agentDisplayName} の Codex App Server が応答しませんでした（タイムアウト）。しばらく待ってから再試行してください。",
            _ =>
                $"{agentDisplayName} のレートリミット情報を Codex App Server から取得できませんでした。codex にログイン済みか確認するか、しばらく待って再試行してください。",
        };
    }

    // 旧形式 snapshot は一覧表示できてしまうため気づかれにくく、Auto-Pause gate（#147）が
    // silent に無効化される（#168）。「更新」の都度、旧形式を書き出しているエージェントの
    // 有無を再評価する.
    private void UpdateLegacySchemaInfoBar(List<string> legacySchemaAgentNames)
    {
        if (legacySchemaAgentNames.Count == 0)
        {
            LegacySchemaInfoBar.IsOpen = false;
            return;
        }

        LegacySchemaInfoBar.Message =
            $"{string.Join("、", legacySchemaAgentNames)} の statusline snapshot が旧形式（schemaVersion なし）のため、"
            + "Auto-Pause は機能しません。statusline フックを更新してください（docs/statusline-integration.md 参照）。";
        LegacySchemaInfoBar.IsOpen = true;
    }

    // Auto-Pause gate（#147）は起動試行時にしか再評価されず、「更新」で fresh な
    // snapshot を取得しても 95% 未満への解除が反映されなかった（#167）。reviewer /
    // reviewed 両スロットの rateLimitAgentId を、実行中プロセス・MCP subscription・
    // queue には作用しない読み取り専用の再評価として反映する。snapshot の取得・
    // 再利用ロジックは RateLimitSnapshotResolver に委譲する（#167 レビュー対応）.
    private async Task RefreshAutoPauseGateAsync(IReadOnlyDictionary<string, Models.RateLimitSnapshot> capturedSnapshots)
    {
        AppSettings settings = _settingsService.Settings;
        TimeSpan freshnessThreshold = TimeSpan.FromMinutes(settings.RateLimitFreshnessThresholdMinutes);
        List<string> gateAgentIds = new[]
            {
                _settingsService.ResolveLauncherRateLimitAgentId(Models.LauncherRole.Reviewer),
                _settingsService.ResolveLauncherRateLimitAgentId(Models.LauncherRole.Reviewed),
            }
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (gateAgentIds.Count == 0)
        {
            return;
        }

        IReadOnlyList<Models.RateLimitSnapshot> gateSnapshots = await _rateLimitSnapshotResolver
            .ResolveAsync(gateAgentIds, capturedSnapshots, CancellationToken.None)
            .ConfigureAwait(true);

        foreach (string agentId in gateAgentIds)
        {
            _autoPauseGate.Evaluate(agentId, gateSnapshots, freshnessThreshold);
        }

        UpdateAutoPauseInfoBar();
    }

    private void OnRateLimitAgentOptionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_isInitializing || e.PropertyName != nameof(Models.RateLimitAgentOption.IsMonitored))
        {
            return;
        }

        List<string> monitoredIds = _rateLimitAgentOptions.Where(o => o.IsMonitored).Select(o => o.Id).ToList();
        _settingsService.UpdateRateLimitMonitoredAgentIds(monitoredIds);
    }

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

        if (info.IsReminderScheduled)
        {
            _rateLimitReminderService.Cancel(info.ReminderKey);
            info.IsReminderScheduled = false;
        }
        else
        {
            _rateLimitReminderService.Schedule(info.ReminderKey, info.Label, info.ResetAt);
            info.IsReminderScheduled = true;
        }
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
            List<string> resourceUris = ResourceUrisBox.Text
                .Split(_resourceUriLineSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct()
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
            if (resourceUris.Count == 0)
            {
                return;
            }

            int timeoutMs = double.IsNaN(TimeoutBox.Value) ? 60000 : (int)TimeoutBox.Value;

            string reviewerPath = ReviewerPathBox.Text;
            string reviewerArguments = ReviewerArgumentsBox.Text;
            string reviewedPath = ReviewedPathBox.Text;
            string reviewedArguments = ReviewedArgumentsBox.Text;
            Dictionary<string, string> repositoryCheckoutMappings =
                Helpers.RepositoryCheckoutMappingParser.Parse(RepositoryCheckoutMappingsBox.Text);
            int launcherTimeoutMs = double.IsNaN(LauncherTimeoutBox.Value) ? 1800000 : (int)LauncherTimeoutBox.Value;

            string reviewerPresetId = Models.LauncherAgentCatalog.ResolvePresetId(reviewerPath, reviewerArguments, Models.LauncherRole.Reviewer);
            string reviewedPresetId = Models.LauncherAgentCatalog.ResolvePresetId(reviewedPath, reviewedArguments, Models.LauncherRole.Reviewed);
            UpdateLauncherPresetComboBoxSelection(ReviewerPresetComboBox, reviewerPresetId);
            UpdateLauncherPresetComboBoxSelection(ReviewedPresetComboBox, reviewedPresetId);

            _settingsService.UpdateSettings(
                commandPath,
                arguments,
                gatewayUrl,
                resourceUris,
                timeoutMs,
                reviewerPath,
                reviewerArguments,
                reviewedPath,
                reviewedArguments,
                launcherTimeoutMs,
                reviewerPresetId,
                reviewedPresetId);
            _settingsService.UpdateRepositoryCheckoutMappings(repositoryCheckoutMappings);
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
                TryOpenUrl(result.ReleaseUrl);
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

    private void TryOpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

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

    private void OnOpenStatuslineDocsClick(object sender, RoutedEventArgs e)
    {
        TryOpenUrl("https://github.com/scottlz0310/squirrel-notifier/blob/main/docs/statusline-integration.md");
    }

    private void OnReviewEventReceived(object? sender, Models.ReviewEvent e)
    {
        bool enqueued = DispatcherQueue.TryEnqueue(() =>
        {
            _reviewEvents.Insert(0, e);
            const int maxEvents = 20;
            if (_reviewEvents.Count > maxEvents)
            {
                _reviewEvents.RemoveAt(_reviewEvents.Count - 1);
            }

            try
            {
                ReviewNotificationContent.SetReviewEvent(e);
                _trayIconService.ShowReviewPopup();
            }
            catch (Exception ex)
            {
                // ポップアップ表示の失敗でプロセスを落とさない。イベントは一覧に残っているため、
                // 原因をログへ残したうえでバルーン通知へフォールバックする（#199）。
                _ = _loggingService.WriteAsync($"[UI] Failed to show review popup: {ex.Message}");
                _trayIconService.ShowNotification(
                    "レビュー通知",
                    $"{e.Reason}: {e.Repository}#{e.PrNumber}",
                    H.NotifyIcon.Core.NotificationIcon.Info);
            }
        });

        if (!enqueued)
        {
            throw new InvalidOperationException("レビューイベントを UI スレッドへ配送できませんでした。");
        }
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
            _reviewEvents.Remove(reviewEvent);
        }
    }

    private void OnOpenPrClick(object sender, RoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase button && button.CommandParameter is string url)
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

    private void OnTrayPopupOpenPrRequested(object? sender, Models.ReviewEvent reviewEvent)
    {
        _trayIconService.CloseReviewPopup();
        if (Helpers.UrlValidator.IsSafeGitHubUrl(
            reviewEvent.PrUrl,
            reviewEvent.Repository,
            reviewEvent.PrNumber))
        {
            TryOpenUrl(reviewEvent.PrUrl);
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

            var dataPackage = new DataPackage();
            dataPackage.SetText(commandLine);
            Clipboard.SetContent(dataPackage);

            ShowCopyFeedback("起動コマンドをクリップボードにコピーしました。", isError: false);
        }
        catch (Exception ex)
        {
            ShowCopyFeedback($"コピーに失敗しました: {ex.Message}", isError: true);
        }
    }

    private void ShowCopyFeedback(string message, bool isError)
    {
        _copyFeedbackCts?.Cancel();
        _copyFeedbackCts?.Dispose();
        _copyFeedbackCts = new CancellationTokenSource();
        CancellationToken token = _copyFeedbackCts.Token;

        CopyFeedbackInfoBar.Severity = isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
        CopyFeedbackInfoBar.Message = message;
        CopyFeedbackInfoBar.IsOpen = true;

        _ = HideCopyFeedbackAfterDelayAsync(token);
    }

    private async Task HideCopyFeedbackAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2.5), token).ConfigureAwait(true);
            CopyFeedbackInfoBar.IsOpen = false;
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer copy feedback; nothing to do
        }
        catch (Exception ex)
        {
            // ウィンドウ終了中などで InfoBar 更新が失敗しても致命的ではないためログのみ
            _ = _loggingService.WriteAsync($"Failed to hide copy feedback InfoBar: {ex.Message}");
        }
    }

    private async Task ExecuteReviewAsync(Models.ReviewEvent reviewEvent, Models.LauncherRole role)
    {
        // Auto-Pause 確認ダイアログ等の await 中は IsRunning がまだ false のため、起動ボタンの
        // 連打で本メソッドが再入し ContentDialog の多重表示（WinUI3 では例外）になる。
        // 再入はダイアログを出さず黙って無視する — ここでダイアログを出すこと自体が
        // 多重表示例外の原因になるため（#147 レビュー指摘）
        if (_isReviewStartPending)
        {
            return;
        }

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

        _isReviewStartPending = true;
        try
        {
            string roleLabel = role == Models.LauncherRole.Reviewer ? "レビューする" : "レビューに対応";
            var viewModel = new ViewModels.AgentExecutionViewModel(
                $"{reviewEvent.Repository}#{reviewEvent.PrNumber}（{roleLabel}）",
                _settingsService.Settings.LiveLogAutoCloseEnabled,
                SecretMasker.CreateDefault(),
                _settingsService.ResolveLauncherProgressEventSupport(role));

            AppSettings settings = _settingsService.Settings;
            string? activeAgentId = _settingsService.ResolveLauncherRateLimitAgentId(role);
            TimeSpan freshnessThreshold = TimeSpan.FromMinutes(settings.RateLimitFreshnessThresholdMinutes);
            ViewModels.RateLimitGaugeViewModel rateLimitGaugeViewModel = new(freshnessThreshold);
            RateLimitSessionMonitor rateLimitSessionMonitor = new(
                _rateLimitSnapshotService,
                new RateLimitDeltaCalculator(),
                settings.RateLimitMonitoredAgentIds,
                activeAgentId,
                freshnessThreshold);
            IReadOnlyList<Models.RateLimitSnapshot> startSnapshots = await rateLimitSessionMonitor.CaptureStartAsync(CancellationToken.None);
            rateLimitGaugeViewModel.Update(settings.RateLimitMonitoredAgentIds, startSnapshots, activeAgentId, []);

            // Auto-Pause gate（#147）: 起動する launcher スロットの agent が危険水域なら
            // 新規起動を拒否する。実行中プロセス・MCP subscription・queue には作用しない
            AutoPauseDecision autoPauseDecision = _autoPauseGate.Evaluate(activeAgentId, startSnapshots, freshnessThreshold);
            UpdateAutoPauseInfoBar();
            if (autoPauseDecision.Status == AutoPauseStatus.Paused
                && !await ConfirmAutoPauseOverrideAsync(autoPauseDecision.PausedLimit!))
            {
                return;
            }

            AgentExecutionSession session = _launcherService.StartSession(reviewEvent, role, CancellationToken.None);

            // 実行の進捗とログはライブログウィンドウ（#144）が逐次表示する。lifecycle
            // （成功時自動クローズ・失敗時保持・クローズ時キャンセル）はウィンドウ側の責務
            var window = new AgentExecutionWindow(session, viewModel, rateLimitGaugeViewModel, rateLimitSessionMonitor, _autoPauseGate, _launcherService.Cancel);
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
        finally
        {
            // StartSession 成功後の同時実行抑止は _launcherService.IsRunning が担う
            _isReviewStartPending = false;
        }
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
        IReadOnlyList<AutoPausedLimit> pausedLimits = _autoPauseGate.PausedLimits;
        if (pausedLimits.Count == 0)
        {
            AutoPauseInfoBar.IsOpen = false;
            return;
        }

        AutoPauseInfoBar.Message =
            string.Join(Environment.NewLine, pausedLimits.Select(paused => paused.BuildReasonText()))
            + Environment.NewLine
            + "fresh なレートリミット情報で使用率 95% 未満を確認すると自動解除されます。";
        AutoPauseInfoBar.IsOpen = true;
    }

    private async void OnEnqueueReviewClick(object sender, RoutedEventArgs e)
    {
        string input = PrReferenceBox.Text;
        if (!Helpers.PrReferenceParser.TryParse(input, out Models.PrReference? reference) || reference == null)
        {
            await ShowAlertDialogAsync(
                "入力エラー",
                "PR URL（https://github.com/owner/repo/pull/123）または owner/repo#123 の形式で入力してください。");
            return;
        }

        string reason = ReasonComboBox.SelectedItem as string ?? "opened";

        EnqueueReviewButton.IsEnabled = false;
        try
        {
            Models.EnqueueReviewResult result = await _enqueueReviewService.EnqueueAsync(reference, reason, CancellationToken.None).ConfigureAwait(true);

            if (result.Success)
            {
                string message = $"{reference.Owner}/{reference.Repo}#{reference.PrNumber} を reason={reason} で登録しました。";
                if (_service.State != SubscriptionState.Running)
                {
                    message += "\n購読が停止中のため、通知は届きません。先に購読を開始してください。";
                }

                await ShowAlertDialogAsync("レビュー登録完了", message).ConfigureAwait(true);
                PrReferenceBox.Text = string.Empty;
            }
            else
            {
                // 認証エラーはログイン導線（#183）へ誘導する。ダイアログを閉じた後、
                // 認証 InfoBar の「mcp-gateway にログイン」から復旧できる。
                AuthRequiredInfoBar.IsOpen = result.IsAuthenticationRequired;
                await ShowAlertDialogAsync("レビュー登録エラー", result.ErrorMessage).ConfigureAwait(true);
            }
        }
        finally
        {
            EnqueueReviewButton.IsEnabled = true;
        }
    }

    private async void OnLoginToGatewayClick(object sender, RoutedEventArgs e)
    {
        await StartGatewayLoginAsync();
    }

    // mcp-gateway の device flow login をアプリ内から開始する（#183）。認証処理自体は
    // mcp-resource-subscriber が担当し、ここでは起動・進行表示・ブラウザ導線・再購読のみ行う。
    private async Task StartGatewayLoginAsync()
    {
        if (_isLoginPending)
        {
            return;
        }

        string gatewayUrl = GatewayUrlBox.Text;
        if (string.IsNullOrWhiteSpace(gatewayUrl)
            || !Uri.TryCreate(gatewayUrl, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            await ShowAlertDialogAsync("設定エラー", "Gateway URL が正しくありません。先に Gateway URL を http(s):// 形式で設定してください。");
            return;
        }

        _isLoginPending = true;
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

        DeviceVerificationInfo? latestInfo = null;

        urlCopyButton.Click += (_, _) =>
        {
            if (latestInfo != null)
            {
                CopyToClipboard(latestInfo.DisplayUri);
            }
        };
        codeCopyButton.Click += (_, _) =>
        {
            if (latestInfo != null)
            {
                CopyToClipboard(latestInfo.UserCode);
            }
        };

        void OnStatus(object? sender, string message)
        {
            _ = DispatcherQueue.TryEnqueue(() => statusText.Text = message);
        }

        void OnVerification(object? sender, DeviceVerificationInfo info)
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                latestInfo = info;
                urlValue.Text = info.DisplayUri;
                urlLabel.Visibility = Visibility.Visible;
                urlValue.Visibility = Visibility.Visible;
                urlCopyButton.Visibility = Visibility.Visible;
                if (!string.IsNullOrEmpty(info.UserCode))
                {
                    codeValue.Text = info.UserCode;
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
            _isLoginPending = false;
            GatewayLoginButton.IsEnabled = true;
        }
    }

    private async Task HandleLoginResultAsync(Models.McpLoginResult result)
    {
        switch (result.Outcome)
        {
            case Models.McpLoginOutcome.Succeeded:
                AuthRequiredInfoBar.IsOpen = false;

                // 認証成功後、購読が停止中または Error なら再購読を開始し、手動レビュー開始を
                // 再試行できる状態へ戻す（#183 AC）。
                string message = "mcp-gateway への認証に成功しました。";
                if (_service.State is SubscriptionState.Stopped or SubscriptionState.Error)
                {
                    _service.Start();
                    message += "\n購読を再開しました。";
                }

                await ShowAlertDialogAsync("ログイン成功", message);
                break;

            case Models.McpLoginOutcome.Cancelled:
                // ユーザー操作による中断のため、追加の通知は出さない。
                break;

            case Models.McpLoginOutcome.TimedOut:
                await ShowAlertDialogAsync("ログインがタイムアウトしました", result.ErrorMessage ?? "認証が時間内に完了しませんでした。");
                break;

            case Models.McpLoginOutcome.Failed:
            default:
                await ShowAlertDialogAsync("ログインに失敗しました", result.ErrorMessage ?? "mcp-gateway へのログインに失敗しました。");
                break;
        }
    }

    private void CopyToClipboard(string text)
    {
        try
        {
            var dataPackage = new DataPackage();
            dataPackage.SetText(text);
            Clipboard.SetContent(dataPackage);
            ShowCopyFeedback("クリップボードにコピーしました。", isError: false);
        }
        catch (Exception ex)
        {
            ShowCopyFeedback($"コピーに失敗しました: {ex.Message}", isError: true);
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
