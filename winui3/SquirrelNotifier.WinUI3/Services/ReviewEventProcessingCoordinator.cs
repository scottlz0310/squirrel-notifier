// <copyright file="ReviewEventProcessingCoordinator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// 受信したレビューイベントの保持、終了確認、自動起動の順序を管理する.
/// </summary>
internal sealed class ReviewEventProcessingCoordinator
{
    private readonly ReviewEventCollectionCoordinator _collectionCoordinator;
    private readonly ReviewEventCleanupCoordinator _cleanupCoordinator;
    private readonly Func<ReviewEvent, Task<ReviewStartResult>> _tryStartAutomaticallyAsync;

    public ReviewEventProcessingCoordinator(
        ReviewEventCollectionCoordinator collectionCoordinator,
        ReviewEventCleanupCoordinator cleanupCoordinator,
        Func<ReviewEvent, Task<ReviewStartResult>> tryStartAutomaticallyAsync)
    {
        _collectionCoordinator = collectionCoordinator ?? throw new ArgumentNullException(nameof(collectionCoordinator));
        _cleanupCoordinator = cleanupCoordinator ?? throw new ArgumentNullException(nameof(cleanupCoordinator));
        _tryStartAutomaticallyAsync = tryStartAutomaticallyAsync ?? throw new ArgumentNullException(nameof(tryStartAutomaticallyAsync));
    }

    /// <summary>
    /// イベントを一覧へ追加し、終了済み PR を除外したうえで自動起動を試みる.
    /// </summary>
    /// <param name="reviewEvent">受信したレビューイベント.</param>
    /// <returns>自動起動結果。終了済み PR の場合は <see langword="null"/>.</returns>
    public async Task<ReviewEventProcessingResult> ProcessAsync(ReviewEvent reviewEvent)
    {
        ArgumentNullException.ThrowIfNull(reviewEvent);

        ReviewEvent? evictedEvent = _collectionCoordinator.Add(reviewEvent);
        _cleanupCoordinator.Track(reviewEvent);
        if (evictedEvent != null)
        {
            _cleanupCoordinator.Untrack(evictedEvent.EventId);
        }

        if (!await _cleanupCoordinator.IsActionAllowedAsync(reviewEvent, CancellationToken.None))
        {
            return new ReviewEventProcessingResult(null);
        }

        ReviewStartResult startResult = await _tryStartAutomaticallyAsync(reviewEvent);
        return new ReviewEventProcessingResult(startResult);
    }
}

/// <summary>
/// レビューイベント処理後に UI が反映する自動起動結果.
/// </summary>
internal sealed record ReviewEventProcessingResult(ReviewStartResult? StartResult)
{
    public bool ShouldNotify => StartResult is not null;
}
