// <copyright file="IRateLimitReminderService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;

namespace SquirrelNotifier.WinUI3.Services;

internal interface IRateLimitReminderService
{
    bool IsScheduled(string id);

    void Schedule(string id, string label, DateTimeOffset resetAt);

    void Cancel(string id);
}
