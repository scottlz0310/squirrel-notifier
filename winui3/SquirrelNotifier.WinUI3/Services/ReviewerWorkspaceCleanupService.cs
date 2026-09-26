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
/// reviewer の終了時に再試行する.
/// </remarks>
internal sealed class ReviewerWorkspaceCleanupService
{
    private readonly string _reviewerRoot;
    private readonly Func<string, int, bool> _isReviewerRunning;
    private readonly LoggingService _loggingService;
    private readonly Dictionary<string, (string Repository, int PrNumber)> _pending = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public ReviewerWorkspaceCleanupService(
        string reviewerRoot,
        Func<string, int, bool> isReviewerRunning,
        LoggingService loggingService)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewerRoot);
        _reviewerRoot = reviewerRoot;
        _isReviewerRunning = isReviewerRunning ?? throw new ArgumentNullException(nameof(isReviewerRunning));
        _loggingService = loggingService ?? throw new ArgumentNullException(nameof(loggingService));
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

        // owner / repo の階層がリンクだと、PR 単位のディレクトリの実体が管理外の場所になるため消さない.
        string? repoDirectory = Path.GetDirectoryName(workspace);
        string? ownerDirectory = Path.GetDirectoryName(repoDirectory);
        foreach (string? ancestor in new[] { ownerDirectory, repoDirectory })
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
        try
        {
            ReviewerWorkspaceCleanupResult result = Cleanup(repository, prNumber);
            SetPending(repository, prNumber, result == ReviewerWorkspaceCleanupResult.SkippedReviewerRunning);
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
            SetPending(repository, prNumber, pending: true);
            await _loggingService.WriteAsync($"reviewer 作業領域の削除に失敗しました。reviewer の終了時に再試行します ({target}): {ex.Message}").ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            await _loggingService.WriteAsync($"reviewer 作業領域の削除対象を特定できません ({target}): {ex.Message}").ConfigureAwait(false);
        }
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
