// <copyright file="IStartupTaskAccessor.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using Windows.ApplicationModel;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>Windows のスタートアップタスク API への境界.</summary>
internal interface IStartupTaskAccessor
{
    Task<StartupTaskState> GetStateAsync();

    Task<StartupTaskState> RequestEnableAsync();

    Task DisableAsync();
}
