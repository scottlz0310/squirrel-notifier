// <copyright file="CopyFeedbackCoordinator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Services;

internal enum CopyFeedbackSeverity
{
    /// <summary>成功通知.</summary>
    Success,

    /// <summary>エラー通知.</summary>
    Error,
}

internal sealed record CopyFeedbackPresentation(
    CopyFeedbackSeverity Severity,
    string Message);

internal sealed record CopyFeedbackExpirationFailure(string Message);

/// <summary>コピー通知の文言、表示状態、表示期限を管理する.</summary>
internal sealed class CopyFeedbackCoordinator : IDisposable
{
    private static readonly TimeSpan _displayDuration = TimeSpan.FromSeconds(2.5);
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private CancellationTokenSource? _expirationCts;
    private bool _isDisposed;

    internal CopyFeedbackCoordinator(Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _delayAsync = delayAsync ?? ((delay, cancellationToken) => Task.Delay(delay, cancellationToken));
    }

    public event EventHandler? Expired;

    public event EventHandler<CopyFeedbackExpirationFailure>? ExpirationFailed;

    public CopyFeedbackPresentation ShowLaunchCommandCopied()
    {
        return Show("起動コマンドをクリップボードにコピーしました。", CopyFeedbackSeverity.Success);
    }

    public CopyFeedbackPresentation ShowTextCopied()
    {
        return Show("クリップボードにコピーしました。", CopyFeedbackSeverity.Success);
    }

    public CopyFeedbackPresentation ShowFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Show($"コピーに失敗しました: {exception.Message}", CopyFeedbackSeverity.Error);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        CancellationTokenSource? expirationCts = _expirationCts;
        _expirationCts = null;
        if (expirationCts is not null)
        {
            expirationCts.Cancel();
            expirationCts.Dispose();
        }
    }

    private CopyFeedbackPresentation Show(string message, CopyFeedbackSeverity severity)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        CancelExpiration();
        CancellationTokenSource expirationCts = new();
        _expirationCts = expirationCts;
        _ = ExpireAsync(expirationCts);

        return new CopyFeedbackPresentation(severity, message);
    }

    private async Task ExpireAsync(CancellationTokenSource expirationCts)
    {
        CancellationToken token = expirationCts.Token;
        try
        {
            await _delayAsync(_displayDuration, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (ReferenceEquals(_expirationCts, expirationCts))
            {
                _expirationCts = null;
                Expired?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            // 新しい通知またはアプリ終了で表示期限が取り消された。
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_expirationCts, expirationCts))
            {
                _expirationCts = null;
            }

            ExpirationFailed?.Invoke(
                this,
                new CopyFeedbackExpirationFailure(
                    $"コピー通知の自動クローズに失敗しました: {exception.Message}"));
        }
        finally
        {
            expirationCts.Dispose();
        }
    }

    private void CancelExpiration()
    {
        CancellationTokenSource? expirationCts = _expirationCts;
        _expirationCts = null;
        expirationCts?.Cancel();
    }
}
