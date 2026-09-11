// <copyright file="UpdateCheckCoordinator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Services;

internal enum UpdateDialogAction
{
    /// <summary>ダイアログを閉じる.</summary>
    Close,

    /// <summary>ダウンロードページを開く.</summary>
    Download,

    /// <summary>起動時の通知からこのバージョンを除外する.</summary>
    Skip,
}

internal sealed record UpdateDialogPresentation(
    string Title,
    string Message,
    string PrimaryButtonText = "",
    string SecondaryButtonText = "",
    string CloseButtonText = "閉じる")
{
    public bool IsUpdateAvailable => PrimaryButtonText.Length > 0;
}

/// <summary>更新チェックの結果解釈、スキップ設定、ダイアログ表示中を含む再入抑止を担当する.</summary>
/// <remarks>UI スレッドから呼び出す。表示のため await 後も呼び出し元のコンテキストを維持する.</remarks>
internal sealed class UpdateCheckCoordinator(
    Func<CancellationToken, Task<AutoUpdateResult>> checkAsync,
    SettingsService settingsService,
    LoggingService loggingService,
    IUrlOpener urlOpener,
    TimeSpan? timeout = null)
{
    private bool _isChecking;

    public async Task CheckAsync(bool isManual, Func<UpdateDialogPresentation, Task<UpdateDialogAction>> showDialogAsync)
    {
        if (_isChecking)
        {
            return;
        }

        _isChecking = true;
        try
        {
            string? errorMessage;
            try
            {
                errorMessage = await CheckCoreAsync(isManual, showDialogAsync);
            }
            catch (Exception ex)
            {
                errorMessage = ex is OperationCanceledException ? "更新チェックがタイムアウトしました。" : ex.Message;
                await loggingService.WriteAsync($"自動更新チェック中にエラーが発生しました: {errorMessage}");
            }

            if (isManual && errorMessage is not null)
            {
                await showDialogAsync(new UpdateDialogPresentation("更新チェックに失敗しました", errorMessage));
            }
        }
        finally
        {
            _isChecking = false;
        }
    }

    private async Task<string?> CheckCoreAsync(
        bool isManual, Func<UpdateDialogPresentation, Task<UpdateDialogAction>> showDialogAsync)
    {
        AutoUpdateResult result;
        using (var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10)))
        {
            result = await checkAsync(cts.Token);
        }

        if (result.ErrorMessage is not null)
        {
            return result.ErrorMessage;
        }

        if (!result.HasUpdate)
        {
            if (isManual)
            {
                await showDialogAsync(new UpdateDialogPresentation(
                    "最新バージョンを利用中です", "新しいバージョンは見つかりませんでした。"));
            }

            return null;
        }

        if (!isManual && !string.IsNullOrEmpty(result.Tag)
            && result.Tag == settingsService.Settings.LastSkippedVersion)
        {
            return null;
        }

        UpdateDialogAction action = await showDialogAsync(new UpdateDialogPresentation(
            "新しいバージョンがあります",
            $"最新バージョン {result.LatestVersion} がリリースされています。ダウンロードページを開きますか？",
            "ダウンロード",
            "このバージョンをスキップ",
            "後で"));
        if (action == UpdateDialogAction.Download)
        {
            if (!urlOpener.TryOpen(result.ReleaseUrl))
            {
                throw new InvalidOperationException("ダウンロードページを開けませんでした。");
            }
        }
        else if (action == UpdateDialogAction.Skip)
        {
            settingsService.UpdateLastSkippedVersion(result.Tag ?? result.LatestVersion.ToString());
        }

        return null;
    }
}
