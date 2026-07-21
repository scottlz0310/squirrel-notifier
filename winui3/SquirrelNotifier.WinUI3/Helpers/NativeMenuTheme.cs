// <copyright file="NativeMenuTheme.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// Win32 ポップアップメニューをシステムのライト／ダークテーマに追従させる（#202）。
/// トレイの右クリックメニューは H.NotifyIcon が <c>ContextMenuMode.PopupMenu</c>（既定）で
/// ネイティブメニューへ変換して表示するため、XAML の <c>ThemeResource</c> も
/// <c>TaskbarIcon.ContextMenuThemeMode</c> も効かず、プロセス既定のライト配色で描画される。
/// ネイティブメニューの配色を変えられるのは uxtheme.dll の ordinal API のみで、
/// エクスプローラー自身もこれを使う。ドキュメント化されていない API のため、
/// 呼び出しに失敗しても機能を落とさず無視する（メニューは従来どおりライト表示になる）.
/// </summary>
[ExcludeFromCodeCoverage]
internal static class NativeMenuTheme
{
    // SetPreferredAppMode は Windows 10 1809（build 17763）で追加された。
    private const int _minimumBuildWithPreferredAppMode = 17763;

    // PreferredAppMode.AllowDark: システムがダークのときだけメニューをダークにする
    // （ForceDark と違い、ライトテーマへ戻したときに追従できる）。
    private const int _allowDark = 1;

    /// <summary>
    /// ネイティブメニューのシステムテーマ追従を有効にする。プロセスにつき一度呼べばよい.
    /// </summary>
    public static void EnableSystemThemeForPopupMenus()
    {
        if (Environment.OSVersion.Version.Build < _minimumBuildWithPreferredAppMode)
        {
            return;
        }

        try
        {
            _ = SetPreferredAppMode(_allowDark);
            FlushMenuThemes();
        }
        catch (EntryPointNotFoundException)
        {
            // ordinal が存在しない Windows。メニューはライト表示のままになる
        }
        catch (DllNotFoundException)
        {
            // uxtheme.dll が無い環境。同上
        }
    }

    /// <summary>
    /// システムのテーマ設定を読み直し、メニューのキャッシュ済みテーマを破棄する。
    /// uxtheme はプロセス起動時のカラーポリシーをキャッシュするため、
    /// RefreshImmersiveColorPolicyState を呼ばないと起動後のテーマ切り替えに追従しない.
    /// </summary>
    public static void Refresh()
    {
        if (Environment.OSVersion.Version.Build < _minimumBuildWithPreferredAppMode)
        {
            return;
        }

        try
        {
            RefreshImmersiveColorPolicyState();
            FlushMenuThemes();
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (DllNotFoundException)
        {
        }
    }

    [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
    private static extern int SetPreferredAppMode(int preferredAppMode);

    [DllImport("uxtheme.dll", EntryPoint = "#136", SetLastError = true)]
    private static extern void FlushMenuThemes();

    [DllImport("uxtheme.dll", EntryPoint = "#104", SetLastError = true)]
    private static extern void RefreshImmersiveColorPolicyState();
}
