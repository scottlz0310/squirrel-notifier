// <copyright file="ReviewerWorkspaceCleanupService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

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
/// 領域に消えて困る作業は残らない.
/// </remarks>
internal sealed class ReviewerWorkspaceCleanupService
{
    private readonly string _reviewerRoot;
    private readonly Func<string, int, bool> _isReviewerRunning;
    private readonly LoggingService _loggingService;

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

    public ReviewerWorkspaceCleanupResult Cleanup(string repository, int prNumber)
    {
        string workspace = ReviewerWorkspaceLayout.GetWorkspaceDirectory(_reviewerRoot, repository, prNumber);
        if (_isReviewerRunning(repository, prNumber))
        {
            return ReviewerWorkspaceCleanupResult.SkippedReviewerRunning;
        }

        if (!Directory.Exists(workspace))
        {
            return ReviewerWorkspaceCleanupResult.NotFound;
        }

        DeleteTree(new DirectoryInfo(workspace));
        return ReviewerWorkspaceCleanupResult.Deleted;
    }

    public void OnPullRequestClosed(object? sender, PullRequestClosedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        _ = CleanupAndLogAsync(e.Repository, e.PrNumber);
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

    private async Task CleanupAndLogAsync(string repository, int prNumber)
    {
        // イベントから fire-and-forget で呼ばれる終端のため、失敗はここでログへ残す.
        string target = $"{repository}#{prNumber}";
        try
        {
            ReviewerWorkspaceCleanupResult result = Cleanup(repository, prNumber);
            string? message = result switch
            {
                ReviewerWorkspaceCleanupResult.Deleted => $"完了した PR の reviewer 作業領域を削除しました: {target}",
                ReviewerWorkspaceCleanupResult.SkippedReviewerRunning => $"reviewer の実行中のため、作業領域を削除しませんでした: {target}",
                _ => null,
            };
            if (message is not null)
            {
                await _loggingService.WriteAsync(message).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await _loggingService.WriteAsync($"reviewer 作業領域の削除に失敗しました ({target}): {ex.Message}").ConfigureAwait(false);
        }
    }
}
