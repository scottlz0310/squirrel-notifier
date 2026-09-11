// <copyright file="WindowIconNativeMethods.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// Win32 のウィンドウアイコン API を呼び出す。.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class WindowIconNativeMethods : IWindowIconNativeMethods
{
    private const uint _windowMessageSetIcon = 0x0080;
    private const uint _imageIcon = 1;
    private const uint _loadFromFile = 0x00000010;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadImage(
        nint hInst,
        string lpszName,
        uint uType,
        int cxDesired,
        int cyDesired,
        uint fuLoad);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    public nint LoadIcon(string iconPath, int pixelSize)
    {
        return LoadImage(nint.Zero, iconPath, _imageIcon, pixelSize, pixelSize, _loadFromFile);
    }

    public void SetIcon(nint windowHandle, WindowIconSize iconSize, nint iconHandle)
    {
        _ = SendMessage(windowHandle, _windowMessageSetIcon, new nint((int)iconSize), iconHandle);
    }
}
