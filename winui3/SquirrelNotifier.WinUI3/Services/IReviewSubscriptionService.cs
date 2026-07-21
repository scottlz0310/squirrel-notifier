// <copyright file="IReviewSubscriptionService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

internal interface IReviewSubscriptionService
{
    SubscriptionState State { get; }

    bool IsAuthenticationRequired { get; }

    Task<SubscriptionStartResult> StartAsync(CancellationToken cancellationToken = default);
}
