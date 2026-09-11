// <copyright file="WindowIconService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// アプリケーションアイコンのパス解決とウィンドウへの設定を調整する。.
/// </summary>
internal sealed class WindowIconService : IWindowIconService
{
    private const string _iconFileName = "squirrel-notifier.ico";
    private readonly string _baseDirectory;
    private readonly Func<string, bool> _fileExists;
    private readonly IWindowIconNativeMethods _nativeMethods;

    public WindowIconService()
        : this(AppContext.BaseDirectory, File.Exists, new WindowIconNativeMethods())
    {
    }

    internal WindowIconService(
        string baseDirectory,
        Func<string, bool> fileExists,
        IWindowIconNativeMethods nativeMethods)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(nativeMethods);

        _baseDirectory = baseDirectory;
        _fileExists = fileExists;
        _nativeMethods = nativeMethods;
    }

    public bool TrySetIcon(nint windowHandle)
    {
        try
        {
            string iconPath = Path.Combine(_baseDirectory, "Assets", _iconFileName);
            if (!_fileExists(iconPath))
            {
                return false;
            }

            bool smallIconSet = ApplyIcon(windowHandle, iconPath, WindowIconSize.Small, 16);
            bool largeIconSet = ApplyIcon(windowHandle, iconPath, WindowIconSize.Large, 32);
            return smallIconSet || largeIconSet;
        }
        catch
        {
            return false;
        }
    }

    private bool ApplyIcon(nint windowHandle, string iconPath, WindowIconSize iconSize, int pixelSize)
    {
        nint iconHandle = _nativeMethods.LoadIcon(iconPath, pixelSize);
        if (iconHandle == nint.Zero)
        {
            return false;
        }

        _nativeMethods.SetIcon(windowHandle, iconSize, iconHandle);
        return true;
    }
}
