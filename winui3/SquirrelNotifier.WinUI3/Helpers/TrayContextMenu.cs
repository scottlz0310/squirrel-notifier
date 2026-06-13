// <copyright file="TrayContextMenu.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace SquirrelNotifier.WinUI3.Helpers;

[ExcludeFromCodeCoverage]
internal sealed class TrayContextMenu : IDisposable
{
    private const int _mfString = 0x00000000;
    private const int _mfSeparator = 0x00000800;
    private const int _tpmLeftAlign = 0x0000;
    private const int _tpmBottomAlign = 0x0020;
    private const int _tpmReturnCmd = 0x0100;
    private nint _hMenu;
    private readonly Dictionary<int, Action> _menuActions = new();
    private int _nextCommandId = 1000; // Start from 1000 to avoid conflicts

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(nint hMenu, int uFlags, nint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(nint hMenu, uint uFlags, int x, int y, int nReserved, nint hWnd, nint prcRect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    private const uint _wmNull = 0x0000;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    public TrayContextMenu()
    {
        _hMenu = CreatePopupMenu();
    }

    public void AddMenuItem(string text, Action action)
    {
        int commandId = _nextCommandId++;
        AppendMenu(_hMenu, _mfString, new nint(commandId), text);
        _menuActions[commandId] = action;
    }

    public void AddSeparator()
    {
        AppendMenu(_hMenu, _mfSeparator, nint.Zero, string.Empty);
    }

    public void Show(nint hwnd)
    {
        SetForegroundWindow(hwnd);
        GetCursorPos(out POINT pt);

        // TPM_RETURNCMD makes TrackPopupMenu return the selected command ID directly
        int selectedId = TrackPopupMenu(_hMenu, _tpmLeftAlign | _tpmBottomAlign | _tpmReturnCmd, pt.X, pt.Y, 0, hwnd, nint.Zero);

        // Post a message to ensure the menu is properly closed
        PostMessage(hwnd, _wmNull, nint.Zero, nint.Zero);

        if (selectedId > 0 && _menuActions.TryGetValue(selectedId, out Action? action))
        {
            action?.Invoke();
        }
    }

    public bool ProcessCommand(int commandId)
    {
        if (_menuActions.TryGetValue(commandId, out Action? action))
        {
            action?.Invoke();
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (_hMenu != nint.Zero)
        {
            DestroyMenu(_hMenu);
            _hMenu = nint.Zero;
        }
    }
}
