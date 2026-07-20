// <copyright file="IBrowserLauncher.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Diagnostics;

namespace SquirrelNotifier.WinUI3.Services;

internal interface IBrowserLauncher
{
    bool OpenUrl(string url);
}

internal sealed class SystemBrowserLauncher : IBrowserLauncher
{
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
