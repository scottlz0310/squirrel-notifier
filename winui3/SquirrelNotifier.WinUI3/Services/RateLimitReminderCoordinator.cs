// <copyright file="RateLimitReminderCoordinator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>レートリミット通知予約の切替と表示状態の同期を担当する.</summary>
internal sealed class RateLimitReminderCoordinator
{
    private readonly IRateLimitReminderService _reminderService;

    public RateLimitReminderCoordinator(IRateLimitReminderService reminderService)
    {
        ArgumentNullException.ThrowIfNull(reminderService);
        _reminderService = reminderService;
    }

    /// <summary>指定したレートリミットの通知予約を切り替える.</summary>
    /// <param name="info">切替対象のレートリミット情報.</param>
    public void Toggle(RateLimitInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        if (info.IsReminderScheduled)
        {
            _reminderService.Cancel(info.ReminderKey);
            info.IsReminderScheduled = false;
            return;
        }

        _reminderService.Schedule(info.ReminderKey, info.Label, info.ResetAt);
        info.IsReminderScheduled = true;
    }
}
