// <copyright file="PackageIdentity.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Runtime.InteropServices;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>実行中のプロセスが MSIX のパッケージ ID を持つか（#569）.</summary>
internal static class PackageIdentity
{
    // パッケージ ID を持たないプロセスで GetCurrentPackageFullName が返すエラー
    private const int _appModelErrorNoPackage = 15700;

    public static bool Exists()
    {
        int length = 0;
        return GetCurrentPackageFullName(ref length, null) != _appModelErrorNoPackage;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);
}
