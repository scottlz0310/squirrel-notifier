// <copyright file="TrayContextMenu.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// トレイの右クリックメニューを Win32 のポップアップメニューとして表示する（#202）。
/// H.NotifyIcon の <c>ContextFlyout</c> は <c>ContextMenuMode.PopupMenu</c> でネイティブメニューへ
/// 変換されるが、その変換は <c>MenuFlyoutItem.IsEnabled</c> も
/// <c>TaskbarIcon.ContextMenuThemeMode</c> も反映しないため、活性制御とテーマ追従の
/// どちらも実現できない。表示のたびにメニューを組み直すことで、購読状態に応じた
/// 活性状態を確実に反映する.
/// </summary>
[ExcludeFromCodeCoverage]
internal static class TrayContextMenu
{
    private const int _mfString = 0x0000;
    private const int _mfGrayed = 0x0001;
    private const int _mfSeparator = 0x0800;

    private const uint _tpmLeftAlign = 0x0000;
    private const uint _tpmBottomAlign = 0x0020;
    private const uint _tpmRightButton = 0x0002;

    // 選択されたコマンド ID を戻り値で受け取る（メニュー用の WndProc を持たなくてよい）
    private const uint _tpmReturnCmd = 0x0100;

    private const uint _wmNull = 0x0000;

    /// <summary>
    /// メニューをカーソル位置に表示し、選択された項目を返す.
    /// </summary>
    /// <param name="ownerWindow">メニューの所有ウィンドウ.</param>
    /// <param name="entries">表示する項目.</param>
    /// <returns>選択されたコマンド。何も選ばれなければ <see langword="null"/>.</returns>
    public static TrayMenuCommand? Show(nint ownerWindow, IReadOnlyList<TrayMenuEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        // システムのテーマが切り替わっている可能性があるため、組み立て前にメニューの
        // テーマキャッシュを破棄する
        NativeMenuTheme.Refresh();

        nint menu = CreatePopupMenu();
        if (menu == nint.Zero)
        {
            throw new InvalidOperationException("トレイメニューの作成に失敗しました。");
        }

        try
        {
            foreach (TrayMenuEntry entry in entries)
            {
                if (entry.IsSeparator)
                {
                    _ = AppendMenu(menu, _mfSeparator, nint.Zero, string.Empty);
                    continue;
                }

                int flags = entry.IsEnabled ? _mfString : _mfString | _mfGrayed;
                _ = AppendMenu(menu, flags, new nint((int)entry.Command), entry.Text);
            }

            // メニューの外側をクリックしたときに閉じるには、所有ウィンドウが前面である必要がある
            _ = SetForegroundWindow(ownerWindow);

            if (!GetCursorPos(out POINT cursor))
            {
                return null;
            }

            int selected = TrackPopupMenuEx(
                menu,
                _tpmLeftAlign | _tpmBottomAlign | _tpmRightButton | _tpmReturnCmd,
                cursor.X,
                cursor.Y,
                ownerWindow,
                nint.Zero);

            // メニューが確実に閉じるようダミーメッセージを送る（Win32 の定石）
            _ = PostMessage(ownerWindow, _wmNull, nint.Zero, nint.Zero);

            return selected > 0 ? (TrayMenuCommand)selected : null;
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(nint hMenu, int uFlags, nint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int TrackPopupMenuEx(nint hMenu, uint uFlags, int x, int y, nint hwnd, nint lptpm);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
}
