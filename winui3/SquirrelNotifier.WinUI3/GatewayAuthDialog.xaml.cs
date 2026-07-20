// <copyright file="GatewayAuthDialog.xaml.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;
using Windows.ApplicationModel.DataTransfer;

namespace SquirrelNotifier.WinUI3;

internal sealed partial class GatewayAuthDialog : ContentDialog
{
    private readonly GatewayAuthService _authService;
    private readonly CancellationTokenSource _cts = new();

    public GatewayAuthDialog(GatewayAuthService authService)
    {
        InitializeComponent();
        _authService = authService;
        Opened += OnDialogOpened;
    }

    private async void OnDialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        var progress = new Progress<GatewayAuthProgress>(UpdateUI);

        CloseButtonClick += (s, e) =>
        {
            _cts.Cancel();
        };

        GatewayAuthProgress result = await _authService.LoginAsync(progress, _cts.Token);
        UpdateUI(result);
    }

    private void UpdateUI(GatewayAuthProgress authProgress)
    {
        switch (authProgress.Stage)
        {
            case GatewayAuthStage.Starting:
                LoginProgressRing.IsActive = true;
                StatusTextBlock.Text = "mcp-gateway 認証を開始しています...";
                CloseButtonText = "キャンセル";
                break;

            case GatewayAuthStage.WaitingForUser:
                LoginProgressRing.IsActive = true;
                StatusTextBlock.Text = "ブラウザで認証を完了してください。";
                CloseButtonText = "キャンセル";

                if (!string.IsNullOrEmpty(authProgress.VerificationUrl))
                {
                    UrlSection.Visibility = Visibility.Visible;
                    UrlTextBox.Text = authProgress.VerificationUrl;
                }

                if (!string.IsNullOrEmpty(authProgress.UserCode))
                {
                    UserCodeSection.Visibility = Visibility.Visible;
                    UserCodeTextBox.Text = authProgress.UserCode;
                }

                if (authProgress.BrowserLaunchFailed)
                {
                    BrowserWarningInfoBar.IsOpen = true;
                }

                break;

            case GatewayAuthStage.Success:
                LoginProgressRing.IsActive = false;
                ProgressSection.Visibility = Visibility.Collapsed;
                StatusTextBlock.Text = "mcp-gateway への認証が正常に完了しました！";
                CloseButtonText = "閉じる";
                DefaultButton = ContentDialogButton.Close;
                break;

            case GatewayAuthStage.Failed:
            case GatewayAuthStage.Cancelled:
            case GatewayAuthStage.Timeout:
                LoginProgressRing.IsActive = false;
                ProgressSection.Visibility = Visibility.Collapsed;
                CloseButtonText = "閉じる";
                DefaultButton = ContentDialogButton.Close;

                ErrorInfoBar.Message = authProgress.ErrorMessage ?? "認証処理を完了できませんでした。";
                ErrorInfoBar.IsOpen = true;
                break;
        }
    }

    private void OnCopyUrlClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(UrlTextBox.Text))
        {
            var package = new DataPackage();
            package.SetText(UrlTextBox.Text);
            Clipboard.SetContent(package);
        }
    }

    private void OnCopyUserCodeClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(UserCodeTextBox.Text))
        {
            var package = new DataPackage();
            package.SetText(UserCodeTextBox.Text);
            Clipboard.SetContent(package);
        }
    }
}
