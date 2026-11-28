// <copyright file="AutoStartServiceFactory.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>実行中の配布方式に合う自動起動の仕組みを選ぶ（#569）.</summary>
internal static class AutoStartServiceFactory
{
    public static IAutoStartService Create() => Create(PackageIdentity.Exists());

    internal static IAutoStartService Create(bool hasPackageIdentity)
        => hasPackageIdentity
            ? new StartupTaskAutoStartService(new WindowsStartupTaskAccessor())
            : new TaskSchedulerService();
}
