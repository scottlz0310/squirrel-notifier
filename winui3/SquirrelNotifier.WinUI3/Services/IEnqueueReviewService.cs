// <copyright file="IEnqueueReviewService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

internal interface IEnqueueReviewService
{
    Task<EnqueueReviewResult> EnqueueAsync(
        PrReference reference,
        string reason,
        CancellationToken cancellationToken);
}
