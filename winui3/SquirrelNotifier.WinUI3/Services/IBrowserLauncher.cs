// <copyright file="IBrowserLauncher.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Diagnostics;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// 既定ブラウザで URL を安全に開くための抽象化インターフェース.
/// </summary>
internal interface IBrowserLauncher
{
    /// <summary>
    /// 指定された URL を既定ブラウザで開きます.
    /// </summary>
    /// <param name="url">開く対象の URL.</param>
    /// <returns>起動に成功した場合は true。失敗した場合は false.</returns>
    bool OpenUrl(string url);
}

/// <summary>
/// OS の既定ブラウザを使用して URL を開く標準実装.
/// </summary>
internal sealed class SystemBrowserLauncher : IBrowserLauncher
{
    /// <inheritdoc/>
    public bool OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
