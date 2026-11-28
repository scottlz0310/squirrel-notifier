// <copyright file="UpdateCheckCoordinator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Services;

internal enum UpdateDialogAction
{
    /// <summary>ダイアログを閉じる.</summary>
    Close,

    /// <summary>主ボタンの操作を実行する（ダウンロードページを開く、更新して再起動する、適用を取りやめる）.</summary>
    Accept,

    /// <summary>起動時の通知からこのバージョンを除外する.</summary>
    Skip,
}

internal sealed record UpdateDialogPresentation(
    string Title,
    string Message,
    string PrimaryButtonText = "",
    string SecondaryButtonText = "",
    string CloseButtonText = "閉じる",
    bool IsWaitingNotice = false)
{
    public bool IsUpdateAvailable => PrimaryButtonText.Length > 0 && !IsWaitingNotice;
}

/// <summary>更新チェックの結果解釈、スキップ設定、ダイアログ表示中を含む再入抑止を担当する.</summary>
/// <remarks>UI スレッドから呼び出す。表示のため await 後も呼び出し元のコンテキストを維持する.</remarks>
/// <param name="checkAsync">更新確認を実行する非同期デリゲート.</param>
/// <param name="settingsService">スキップ設定等を保持する設定サービス.</param>
/// <param name="loggingService">診断ログ出力サービス.</param>
/// <param name="urlOpener">リリースページを開く URL オープナー.</param>
/// <param name="timeout">更新確認のタイムアウト時間（省略時は既定値）.</param>
/// <param name="applier">アプリ内で更新を適用できる配布方式の場合の適用先。無ければリリースページを開く.</param>
internal sealed class UpdateCheckCoordinator(
    Func<CancellationToken, Task<AutoUpdateResult>> checkAsync,
    SettingsService settingsService,
    LoggingService loggingService,
    IUrlOpener urlOpener,
    TimeSpan? timeout = null,
    IUpdateApplier? applier = null)
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

    private static async Task ShowWaitingAsync(
        IUpdateApplier waiting, Func<UpdateDialogPresentation, Task<UpdateDialogAction>> showDialogAsync)
    {
        UpdateDialogAction action = await showDialogAsync(new UpdateDialogPresentation(
            "更新の適用を待っています",
            "実行中のレビューがなくなったら、更新して再起動します。それまで新しいレビューは開始しません。",
            "適用を取りやめる",
            IsWaitingNotice: true));
        if (action == UpdateDialogAction.Accept)
        {
            waiting.Cancel();
        }
    }

    private async Task<string?> CheckCoreAsync(
        bool isManual, Func<UpdateDialogPresentation, Task<UpdateDialogAction>> showDialogAsync)
    {
        if (applier is { IsPending: true })
        {
            if (isManual && applier.IsWaiting)
            {
                await ShowWaitingAsync(applier, showDialogAsync);
            }
            else if (isManual)
            {
                await showDialogAsync(new UpdateDialogPresentation(
                    "更新を適用しています", "ダウンロードと適用が終わると、自動で再起動します。"));
            }

            return null;
        }

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

        string question = applier is null
            ? "ダウンロードページを開きますか？"
            : "更新して再起動しますか？実行中のレビューがある場合は、終了を待って適用します。";
        UpdateDialogAction action = await showDialogAsync(new UpdateDialogPresentation(
            "新しいバージョンがあります",
            $"最新バージョン {result.LatestVersion} がリリースされています。{question}",
            applier is null ? "ダウンロード" : "更新して再起動",
            "このバージョンをスキップ",
            "後で"));
        if (action == UpdateDialogAction.Accept)
        {
            if (applier is not null)
            {
                await applier.RequestAsync();
                if (applier.IsWaiting)
                {
                    await ShowWaitingAsync(applier, showDialogAsync);
                }
            }
            else if (!urlOpener.TryOpen(result.ReleaseUrl))
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
