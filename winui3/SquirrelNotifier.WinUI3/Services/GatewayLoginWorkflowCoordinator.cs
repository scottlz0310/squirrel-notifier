// <copyright file="GatewayLoginWorkflowCoordinator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Threading;
using System.Threading.Tasks;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>device flow ログインの進行を実行するクライアント.</summary>
internal interface IGatewayLoginService
{
    /// <summary>認証の進行状況が変化したときに発火する.</summary>
    event EventHandler<string>? StatusChanged;

    /// <summary>device flow の承認情報を受信したときに発火する.</summary>
    event EventHandler<DeviceVerificationInfo>? VerificationReceived;

    /// <summary>ログインを実行する.</summary>
    /// <param name="cancellationToken">ユーザーによるキャンセルを伝えるトークン.</param>
    /// <returns>最終的なログイン結果.</returns>
    Task<McpLoginResult> LoginAsync(CancellationToken cancellationToken);
}

/// <summary>UI スレッドへ操作を配送する境界.</summary>
/// <param name="action">UI スレッドで実行する操作.</param>
/// <returns>配送を受け付けた場合は <see langword="true"/>.</returns>
internal delegate bool GatewayLoginUiDispatcher(Action action);

/// <summary>ログイン進行ダイアログに対する UI 境界.</summary>
internal sealed record GatewayLoginDialogPort(
    Func<Task> ShowAsync,
    Action Hide,
    GatewayLoginUiDispatcher DispatchToUi,
    Action<string> SetStatus,
    Action<DeviceVerificationView> SetVerification);

/// <summary>ログインの開始・完了時に UI へ適用する操作.</summary>
internal sealed record GatewayLoginUiActions(
    Action<bool> SetLoginButtonEnabled,
    Action CloseAuthRequiredInfoBar,
    Action RestartSubscription,
    Func<string, string, Task> ShowAlertAsync);

/// <summary>ログイン進行ダイアログを生成する UI 境界.</summary>
/// <param name="session">ダイアログの Opened とコピー操作を接続するセッション.</param>
/// <returns>生成済みダイアログの UI 境界.</returns>
internal delegate GatewayLoginDialogPort GatewayLoginDialogFactory(GatewayLoginDialogSession session);

/// <summary>
/// mcp-gateway の device flow ログインにおける開始判定、進行ダイアログ、キャンセル、結果適用を管理する.
/// <c>ContentDialog</c> と UI スレッドへの配送は <see cref="GatewayLoginDialogPort"/> として受け取り、
/// 認証 I/O は <see cref="IGatewayLoginService"/> に委譲する.
/// </summary>
internal sealed class GatewayLoginWorkflowCoordinator(
    GatewayLoginCoordinator gatewayLoginCoordinator,
    Func<IGatewayLoginService> loginServiceFactory,
    LoggingService loggingService)
{
    /// <summary>ログインを開始し、完了結果を UI へ適用する.</summary>
    /// <param name="gatewayUrl">設定欄に入力された Gateway URL.</param>
    /// <param name="subscriptionState">ログイン完了時の購読状態.</param>
    /// <param name="createDialog">進行ダイアログを生成する UI 境界.</param>
    /// <param name="uiActions">ボタン、InfoBar、購読、通知ダイアログを反映する UI 境界.</param>
    /// <returns>非同期操作を表すタスク.</returns>
    public async Task StartAsync(
        string? gatewayUrl,
        SubscriptionState subscriptionState,
        GatewayLoginDialogFactory createDialog,
        GatewayLoginUiActions uiActions)
    {
        ArgumentNullException.ThrowIfNull(createDialog);
        ArgumentNullException.ThrowIfNull(uiActions);

        GatewayLoginStartDecision decision = gatewayLoginCoordinator.TryBeginLogin(gatewayUrl);
        if (!decision.CanStart)
        {
            if (decision.ErrorTitle is string errorTitle)
            {
                await uiActions.ShowAlertAsync(errorTitle, decision.ErrorMessage!).ConfigureAwait(true);
            }

            return;
        }

        uiActions.SetLoginButtonEnabled(false);
        try
        {
            var session = new GatewayLoginDialogSession(loginServiceFactory(), loggingService);
            GatewayLoginDialogPort dialog = createDialog(session);
            McpLoginResult result = await session.RunAsync(dialog).ConfigureAwait(true);
            GatewayLoginPresentation presentation = GatewayLoginCoordinator.DescribeResult(result, subscriptionState);

            if (presentation.CloseAuthRequiredInfoBar)
            {
                uiActions.CloseAuthRequiredInfoBar();
            }

            if (presentation.RestartSubscription)
            {
                uiActions.RestartSubscription();
            }

            if (presentation.DialogTitle is string title)
            {
                await uiActions.ShowAlertAsync(title, presentation.DialogMessage!).ConfigureAwait(true);
            }
        }
        finally
        {
            gatewayLoginCoordinator.EndLogin();
            uiActions.SetLoginButtonEnabled(true);
        }
    }
}

/// <summary>
/// 1 回のログイン進行ダイアログの状態を保持する。イベント購読、キャンセル、Opened 前のクローズ保留、
/// コピー用の承認情報を所有し、UI の具体的な要素には依存しない.
/// </summary>
internal sealed class GatewayLoginDialogSession(IGatewayLoginService loginService, LoggingService loggingService)
{
    private readonly DeferredDialogCloseGate _closeGate = new();
    private GatewayLoginDialogPort? _dialog;
    private DeviceVerificationView? _latestView;

    /// <summary>ダイアログが開き終わったことを通知する.</summary>
    public void OnDialogOpened()
    {
        GatewayLoginDialogPort? dialog = _dialog;
        if (dialog is not null && _closeGate.MarkOpened())
        {
            _ = loggingService.WriteAsync(
                "[UI] ログインダイアログの Opened 後に保留中のクローズ要求を実行します。");
            dialog.Hide();
        }
    }

    /// <summary>最新の承認 URL をコピーする.</summary>
    /// <param name="copyText">クリップボードへ値を渡す UI 境界.</param>
    public void CopyVerificationUrl(Action<string> copyText)
    {
        ArgumentNullException.ThrowIfNull(copyText);

        if (_latestView is DeviceVerificationView view)
        {
            copyText(view.Url);
        }
    }

    /// <summary>最新の認証コードをコピーする.</summary>
    /// <param name="copyText">クリップボードへ値を渡す UI 境界.</param>
    public void CopyUserCode(Action<string> copyText)
    {
        ArgumentNullException.ThrowIfNull(copyText);

        if (_latestView?.UserCode is string userCode)
        {
            copyText(userCode);
        }
    }

    /// <summary>ダイアログの表示、ログイン、キャンセル、イベント購読を一つのライフサイクルとして実行する.</summary>
    /// <param name="dialog">進行ダイアログの UI 境界.</param>
    /// <returns>ログインの最終結果.</returns>
    public async Task<McpLoginResult> RunAsync(GatewayLoginDialogPort dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        _dialog = dialog;
        using var cts = new CancellationTokenSource();
        loginService.StatusChanged += OnStatusChanged;
        loginService.VerificationReceived += OnVerificationReceived;
        try
        {
            Task showTask = dialog.ShowAsync();
            Task<McpLoginResult> loginTask = loginService.LoginAsync(cts.Token);
            _ = loginTask.ContinueWith(
                _ => RequestDialogCloseOnUiThread(),
                TaskScheduler.Default);

            await showTask.ConfigureAwait(true);
            if (!loginTask.IsCompleted)
            {
                cts.Cancel();
            }

            return await loginTask.ConfigureAwait(true);
        }
        finally
        {
            loginService.StatusChanged -= OnStatusChanged;
            loginService.VerificationReceived -= OnVerificationReceived;
            _dialog = null;
        }
    }

    private void OnStatusChanged(object? sender, string message)
    {
        GatewayLoginDialogPort? dialog = _dialog;
        if (dialog is not null)
        {
            _ = dialog.DispatchToUi(() => dialog.SetStatus(message));
        }
    }

    private void OnVerificationReceived(object? sender, DeviceVerificationInfo info)
    {
        DeviceVerificationView view = GatewayLoginCoordinator.DescribeVerification(info);
        GatewayLoginDialogPort? dialog = _dialog;
        if (dialog is not null)
        {
            _ = dialog.DispatchToUi(() =>
            {
                _latestView = view;
                dialog.SetVerification(view);
            });
        }
    }

    private void RequestDialogCloseOnUiThread()
    {
        GatewayLoginDialogPort? dialog = _dialog;
        if (dialog is null)
        {
            return;
        }

        bool enqueued = dialog.DispatchToUi(() =>
        {
            if (_closeGate.RequestClose())
            {
                dialog.Hide();
            }
        });
        if (!enqueued)
        {
            _ = loggingService.WriteAsync(
                "[UI] ログインダイアログのクローズ要求を UI スレッドへ配送できませんでした。");
        }
    }
}
