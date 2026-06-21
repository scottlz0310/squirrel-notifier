// <copyright file="TrayIconService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace SquirrelNotifier.WinUI3.Services;

[ExcludeFromCodeCoverage]
internal sealed class TrayIconService : IDisposable
{
    private const int _wmApp = 0x8000;
    private const int _wmTrayIcon = _wmApp + 1;
    private const int _nimAdd = 0x00000000;
    private const int _nimModify = 0x00000001;
    private const int _nimDelete = 0x00000002;
    private const int _nifMessage = 0x00000001;
    private const int _nifIcon = 0x00000002;
    private const int _nifTip = 0x00000004;
    private const int _nifInfo = 0x00000010;
    private const int _niifWarning = 0x00000002;
    private const int _wmLButtonUp = 0x0202;
    private const int _wmRButtonUp = 0x0205;

    private readonly nint _hwnd;
    private readonly uint _callbackMessage = _wmTrayIcon;
    private bool _isAdded;
    private nint _hIcon;
    private string _currentIconPath = string.Empty;

    public event EventHandler? LeftClick;

    public event EventHandler? RightClick;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int CbSize;
        public nint HWnd;
        public uint UID;
        public uint UFlags;
        public uint UCallbackMessage;
        public nint HIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string SzTip;
        public uint DwState;
        public uint DwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string SzInfo;
        public uint UTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string SzInfoTitle;
        public uint DwInfoFlags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern nint LoadIcon(nint hInstance, nint lpIconName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadImage(nint hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    private const uint _imageIcon = 1;
    private const uint _lrLoadFromFile = 0x00000010;

    public TrayIconService(Window window)
    {
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
    }

    public bool AddIcon(string tooltip)
    {
        if (_isAdded)
        {
            return true;
        }

        // Try to load custom icon from Assets folder
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "squirrel-notifier.ico");
        if (File.Exists(iconPath))
        {
            _hIcon = LoadImage(nint.Zero, iconPath, _imageIcon, 16, 16, _lrLoadFromFile);
            _currentIconPath = iconPath;
        }

        // Fallback to default application icon if custom icon fails to load
        if (_hIcon == nint.Zero)
        {
            nint hInstance = GetModuleHandle(null);
            _hIcon = LoadIcon(hInstance, new nint(32512)); // IDI_APPLICATION
        }

        var nid = new NOTIFYICONDATA
        {
            CbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            HWnd = _hwnd,
            UID = 1,
            UFlags = _nifMessage | _nifIcon | _nifTip,
            UCallbackMessage = _callbackMessage,
            HIcon = _hIcon,
            SzTip = tooltip,
        };

        _isAdded = Shell_NotifyIcon(_nimAdd, ref nid);
        return _isAdded;
    }

    public bool UpdateIcon(string iconFileName)
    {
        if (!_isAdded)
        {
            return false;
        }

        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", iconFileName);
        if (!File.Exists(iconPath) || iconPath == _currentIconPath)
        {
            return false;
        }

        nint newIcon = LoadImage(nint.Zero, iconPath, _imageIcon, 16, 16, _lrLoadFromFile);
        if (newIcon == nint.Zero)
        {
            return false;
        }

        nint oldIcon = _hIcon;
        _hIcon = newIcon;
        _currentIconPath = iconPath;

        var nid = new NOTIFYICONDATA
        {
            CbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            HWnd = _hwnd,
            UID = 1,
            UFlags = _nifIcon,
            HIcon = _hIcon,
        };

        bool result = Shell_NotifyIcon(_nimModify, ref nid);
        if (oldIcon != nint.Zero)
        {
            DestroyIcon(oldIcon);
        }

        return result;
    }

    public bool ShowBalloonTip(string title, string text)
    {
        if (!_isAdded)
        {
            return false;
        }

        var nid = new NOTIFYICONDATA
        {
            CbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            HWnd = _hwnd,
            UID = 1,
            UFlags = _nifInfo,
            SzInfoTitle = title,
            SzInfo = text,
            DwInfoFlags = _niifWarning,
        };

        return Shell_NotifyIcon(_nimModify, ref nid);
    }

    public bool UpdateTooltip(string tooltip)
    {
        if (!_isAdded)
        {
            return false;
        }

        var nid = new NOTIFYICONDATA
        {
            CbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            HWnd = _hwnd,
            UID = 1,
            UFlags = _nifTip,
            SzTip = tooltip,
        };

        return Shell_NotifyIcon(_nimModify, ref nid);
    }

    public bool RemoveIcon()
    {
        if (!_isAdded)
        {
            return true;
        }

        var nid = new NOTIFYICONDATA
        {
            CbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            HWnd = _hwnd,
            UID = 1,
        };

        _isAdded = !Shell_NotifyIcon(_nimDelete, ref nid);
        return !_isAdded;
    }

    public bool ProcessWindowMessage(uint msg, nint wParam, nint lParam)
    {
        if (msg != _callbackMessage)
        {
            return false;
        }

        switch (lParam.ToInt32())
        {
            case _wmLButtonUp:
                LeftClick?.Invoke(this, EventArgs.Empty);
                return true;
            case _wmRButtonUp:
                RightClick?.Invoke(this, EventArgs.Empty);
                return true;
        }

        return false;
    }

    public void Dispose()
    {
        RemoveIcon();
        if (_hIcon != nint.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = nint.Zero;
        }
    }
}
