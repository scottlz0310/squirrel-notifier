// <copyright file="ReviewerWorkspaceCleanupService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Globalization;
using SquirrelNotifier.WinUI3.Helpers;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>reviewer 作業領域の片付けの結果.</summary>
internal enum ReviewerWorkspaceCleanupResult
{
    /// <summary>作業領域を削除した.</summary>
    Deleted,

    /// <summary>作業領域が存在しなかった.</summary>
    NotFound,

    /// <summary>同じ PR の reviewer が実行中のため削除しなかった.</summary>
    SkippedReviewerRunning,
}

/// <summary>
/// 完了した PR の reviewer 作業領域（clone や一時ファイル）を削除する（#403）.
/// </summary>
/// <remarks>
/// 削除対象は <see cref="ReviewerWorkspaceLayout"/> が組み立てる PR 単位のディレクトリだけで、
/// %TEMP% などアプリ管理外の場所を推測して消すことはしない。reviewer はコミットも push もしないため、
/// 領域に消えて困る作業は残らない。実行中のため見送った PR と削除に失敗した PR は保留し、
/// reviewer の終了時に再試行する。Recent review events から外れた PR や、アプリの再起動で保留が失われた
/// PR の領域は、最終起動から一定期間（TTL）が過ぎた時点で、起動時と定期実行の回収で削除する.
/// </remarks>
internal sealed class ReviewerWorkspaceCleanupService : IAsyncDisposable
{
    private static readonly TimeSpan _defaultTimeToLive = TimeSpan.FromDays(7);
    private static readonly TimeSpan _defaultSweepInterval = TimeSpan.FromHours(24);
    private readonly string _reviewerRoot;
    private readonly Func<string, int, bool> _isReviewerRunning;
    private readonly LoggingService _loggingService;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _timeToLive;
    private readonly TimeSpan _sweepInterval;
    private readonly Dictionary<string, (string Repository, int PrNumber)> _pending = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private Action? _detach;
    private Task? _sweepTask;

    public ReviewerWorkspaceCleanupService(
        string reviewerRoot,
        Func<string, int, bool> isReviewerRunning,
        LoggingService loggingService,
        TimeProvider? timeProvider = null,
        TimeSpan? timeToLive = null,
        TimeSpan? sweepInterval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewerRoot);
        _reviewerRoot = reviewerRoot;
        _isReviewerRunning = isReviewerRunning ?? throw new ArgumentNullException(nameof(isReviewerRunning));
        _loggingService = loggingService ?? throw new ArgumentNullException(nameof(loggingService));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _timeToLive = timeToLive ?? _defaultTimeToLive;
        _sweepInterval = sweepInterval ?? _defaultSweepInterval;
    }

    /// <summary>
    /// PR 完了の通知と reviewer の終了に接続し、TTL による回収を開始する.
    /// </summary>
    /// <param name="settingsDirectory">作業領域のルートを含む設定ディレクトリ.</param>
    /// <param name="launcherService">実行中の判定と終了通知の元.</param>
    /// <param name="cleanupCoordinator">PR 完了の通知元.</param>
    /// <param name="loggingService">結果の記録先.</param>
    /// <returns>破棄すると接続を解除して回収を止めるサービス.</returns>
    public static ReviewerWorkspaceCleanupService Attach(
        string settingsDirectory,
        ReviewLauncherService launcherService,
        ReviewEventCleanupCoordinator cleanupCoordinator,
        LoggingService loggingService)
    {
        ArgumentNullException.ThrowIfNull(launcherService);
        ArgumentNullException.ThrowIfNull(cleanupCoordinator);
        var service = new ReviewerWorkspaceCleanupService(
            ReviewerWorkspaceLayout.GetReviewerRoot(settingsDirectory),
            launcherService.IsReviewerRunningFor,
            loggingService);
        cleanupCoordinator.PullRequestClosed += service.OnPullRequestClosed;
        launcherService.RunCompleted += service.OnReviewerRunCompleted;
        service._detach = () =>
        {
            cleanupCoordinator.PullRequestClosed -= service.OnPullRequestClosed;
            launcherService.RunCompleted -= service.OnReviewerRunCompleted;
        };
        service.Start();
        return service;
    }

    public void Start()
    {
        if (_sweepTask is not null)
        {
            return;
        }

        _sweepTask = RunSweepLoopAsync(_cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _detach?.Invoke();
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_sweepTask is not null)
        {
            try
            {
                await _sweepTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 終了時のキャンセルは正常系.
            }
        }

        _cts.Dispose();
    }

    internal int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count;
            }
        }
    }

    public ReviewerWorkspaceCleanupResult Cleanup(string repository, int prNumber)
    {
        string workspace = ReviewerWorkspaceLayout.GetWorkspaceDirectory(_reviewerRoot, repository, prNumber);
        if (_isReviewerRunning(repository, prNumber))
        {
            return ReviewerWorkspaceCleanupResult.SkippedReviewerRunning;
        }

        // ルート・owner・repo の階層がリンクだと、PR 単位のディレクトリの実体が管理外の場所になるため消さない.
        string? repoDirectory = Path.GetDirectoryName(workspace);
        string? ownerDirectory = Path.GetDirectoryName(repoDirectory);
        foreach (string? ancestor in new[] { _reviewerRoot, ownerDirectory, repoDirectory })
        {
            if (ancestor is not null && IsReparsePoint(ancestor))
            {
                throw new IOException($"作業領域の親ディレクトリがリンクのため削除しません: {ancestor}");
            }
        }

        var directory = new DirectoryInfo(workspace);
        if (!directory.Exists)
        {
            return ReviewerWorkspaceCleanupResult.NotFound;
        }

        // 作業領域そのものがリンクなら、リンク先をたどらずリンク自体だけを消す.
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            directory.Delete();
        }
        else
        {
            DeleteTree(directory);
        }

        return ReviewerWorkspaceCleanupResult.Deleted;
    }

    public void OnPullRequestClosed(object? sender, PullRequestClosedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        _ = CleanupAndLogAsync(e.Repository, e.PrNumber);
    }

    public void OnReviewerRunCompleted(object? sender, EventArgs e)
        => _ = RetryPendingAsync();

    // 最終起動（作業領域ディレクトリの更新時刻）から TTL を過ぎた PR の領域を削除する。
    // 保留中の PR もあわせて再試行する.
    internal async Task SweepExpiredAsync()
    {
        var root = new DirectoryInfo(_reviewerRoot);
        if (!root.Exists)
        {
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        List<(string Repository, int PrNumber)> expired = new();
        foreach (DirectoryInfo owner in root.EnumerateDirectories())
        {
            foreach (DirectoryInfo repo in owner.EnumerateDirectories())
            {
                foreach (DirectoryInfo pr in repo.EnumerateDirectories())
                {
                    if (int.TryParse(pr.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int prNumber)
                        && ReviewerWorkspaceLayout.IsExpired(pr.LastWriteTimeUtc, now, _timeToLive))
                    {
                        expired.Add(($"{owner.Name}/{repo.Name}", prNumber));
                    }
                }
            }
        }

        foreach ((string repository, int prNumber) in expired)
        {
            await CleanupAndLogAsync(repository, prNumber).ConfigureAwait(false);
        }

        await RetryPendingAsync().ConfigureAwait(false);
    }

    internal async Task RetryPendingAsync()
    {
        (string Repository, int PrNumber)[] targets;
        lock (_lock)
        {
            targets = _pending.Values.ToArray();
        }

        foreach ((string repository, int prNumber) in targets)
        {
            await CleanupAndLogAsync(repository, prNumber).ConfigureAwait(false);
        }
    }

    internal async Task CleanupAndLogAsync(string repository, int prNumber)
    {
        // イベントから fire-and-forget で呼ばれる終端のため、失敗はここでログへ残し、再試行の対象に残す.
        string target = $"{repository}#{prNumber}";

        // 実行中の判定より前に保留へ登録する。判定の直後に reviewer が終了しても、
        // 終了時の再試行（RunCompleted）が必ずこの PR を拾えるようにするため.
        SetPending(repository, prNumber, pending: true);
        try
        {
            ReviewerWorkspaceCleanupResult result = Cleanup(repository, prNumber);
            if (result != ReviewerWorkspaceCleanupResult.SkippedReviewerRunning)
            {
                SetPending(repository, prNumber, pending: false);
            }

            string? message = result switch
            {
                ReviewerWorkspaceCleanupResult.Deleted => $"完了した PR の reviewer 作業領域を削除しました: {target}",
                ReviewerWorkspaceCleanupResult.SkippedReviewerRunning => $"reviewer の実行中のため、作業領域の削除を終了後へ見送りました: {target}",
                _ => null,
            };
            if (message is not null)
            {
                await _loggingService.WriteAsync(message).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _loggingService.WriteAsync($"reviewer 作業領域の削除に失敗しました。reviewer の終了時に再試行します ({target}): {ex.Message}").ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            SetPending(repository, prNumber, pending: false);
            await _loggingService.WriteAsync($"reviewer 作業領域の削除対象を特定できません ({target}): {ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task RunSweepLoopAsync(CancellationToken cancellationToken)
    {
        // 起動時に 1 回、その後は一定間隔で回収する.
        using var timer = new PeriodicTimer(_sweepInterval, _timeProvider);
        do
        {
            try
            {
                await SweepExpiredAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await _loggingService.WriteAsync($"reviewer 作業領域の一覧を取得できませんでした: {ex.Message}").ConfigureAwait(false);
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    private static bool IsReparsePoint(string path)
    {
        var info = new DirectoryInfo(path);
        return info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    private static void DeleteTree(DirectoryInfo directory)
    {
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
        {
            // リンク（symlink / junction）は先をたどらず、リンク自体だけを消す。
            // clone 内に管理外を指すリンクがあっても、リンク先を巻き込まないため.
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
                entry.Delete();
                continue;
            }

            if (entry is DirectoryInfo child)
            {
                DeleteTree(child);
                continue;
            }

            // git の object / pack は読み取り専用で作られ、そのままでは削除に失敗する.
            entry.Attributes &= ~FileAttributes.ReadOnly;
            entry.Delete();
        }

        directory.Attributes &= ~FileAttributes.ReadOnly;
        directory.Delete();
    }

    private void SetPending(string repository, int prNumber, bool pending)
    {
        string key = $"{repository.Trim().ToUpperInvariant()}#{prNumber.ToString(CultureInfo.InvariantCulture)}";
        lock (_lock)
        {
            if (pending)
            {
                _pending[key] = (repository, prNumber);
            }
            else
            {
                _pending.Remove(key);
            }
        }
    }
}
