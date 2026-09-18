// <copyright file="IReviewCycleStore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>PR 単位のレビューサイクル状態を永続化する.</summary>
internal interface IReviewCycleStore
{
    Task<ReviewCycleState?> TryGetAsync(
        string repository,
        int prNumber,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        ReviewCycleState state,
        CancellationToken cancellationToken = default);
}
