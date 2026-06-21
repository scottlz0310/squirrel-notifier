// <copyright file="ITaskSchedulerService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Services;

internal interface ITaskSchedulerService
{
    Task<TaskRegistrationStatus> GetStatusAsync();

    Task RegisterAsync();

    Task UnregisterAsync();

    Task RepairAsync();
}

internal enum TaskRegistrationStatus
{
    /// <summary>タスクスケジューラに未登録。.</summary>
    NotRegistered,

    /// <summary>タスクスケジューラに登録済み。.</summary>
    Registered,
}
