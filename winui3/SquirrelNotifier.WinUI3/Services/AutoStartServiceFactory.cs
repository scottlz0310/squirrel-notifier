// <copyright file="AutoStartServiceFactory.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Runtime.InteropServices;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>実行中の配布方式に合う自動起動の仕組みを選ぶ（#569）.</summary>
internal static class AutoStartServiceFactory
{
    // パッケージ ID を持たないプロセスで GetCurrentPackageFullName が返すエラー
    private const int _appModelErrorNoPackage = 15700;

    public static IAutoStartService Create() => Create(HasPackageIdentity());

    internal static IAutoStartService Create(bool hasPackageIdentity)
        => hasPackageIdentity
            ? new StartupTaskAutoStartService(new WindowsStartupTaskAccessor())
            : new TaskSchedulerService();

    internal static bool HasPackageIdentity()
    {
        int length = 0;
        return GetCurrentPackageFullName(ref length, null) != _appModelErrorNoPackage;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);
}
