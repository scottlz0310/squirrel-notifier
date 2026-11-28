// <copyright file="WindowsStartupTaskAccessor.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using Windows.ApplicationModel;

namespace SquirrelNotifier.WinUI3.Services;

// StartupTask はパッケージ ID を持つプロセスでだけ動くため、テストプロセスからは実行できない
[ExcludeFromCodeCoverage]
internal sealed class WindowsStartupTaskAccessor : IStartupTaskAccessor
{
    public async Task<StartupTaskState> GetStateAsync() => (await GetTaskAsync()).State;

    public async Task<StartupTaskState> RequestEnableAsync() => await (await GetTaskAsync()).RequestEnableAsync();

    public async Task DisableAsync() => (await GetTaskAsync()).Disable();

    private static async Task<StartupTask> GetTaskAsync() => await StartupTask.GetAsync(StartupTaskAutoStartService.TaskId);
}
