// <copyright file="ICacheService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

internal interface ICacheService
{
    Task<NotificationCache> LoadAsync();

    Task SaveAsync(NotificationCache cache);
}
