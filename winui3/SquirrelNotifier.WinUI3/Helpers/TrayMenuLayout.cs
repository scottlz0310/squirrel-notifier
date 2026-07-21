// <copyright file="TrayMenuLayout.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Collections.Generic;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>トレイの右クリックメニューの項目.</summary>
internal enum TrayMenuCommand
{
    /// <summary>区切り線（コマンドではない）.</summary>
    Separator = 0,

    /// <summary>メインウィンドウを開く.</summary>
    Open = 1,

    /// <summary>購読を開始する.</summary>
    Start = 2,

    /// <summary>購読を停止する.</summary>
    Stop = 3,

    /// <summary>アプリの更新を確認する.</summary>
    CheckForUpdates = 4,

    /// <summary>アプリを終了する.</summary>
    Exit = 5,
}

/// <summary>
/// トレイメニューの 1 項目.
/// </summary>
/// <param name="Command">項目に対応するコマンド.</param>
/// <param name="Text">表示テキスト（区切り線では空）.</param>
/// <param name="IsEnabled">選択できるか.</param>
internal readonly record struct TrayMenuEntry(TrayMenuCommand Command, string Text, bool IsEnabled)
{
    /// <summary>Gets a value indicating whether この項目が区切り線か.</summary>
    public bool IsSeparator => Command == TrayMenuCommand.Separator;
}

/// <summary>
/// 購読状態からトレイメニューの構成を決める（#202）。Win32 メニューの描画から切り離し、
/// 項目と活性状態だけを純関数で決めることで単体テストできるようにする.
/// </summary>
internal static class TrayMenuLayout
{
    /// <summary>
    /// 現在の購読状態に対応するメニュー構成を返す.
    /// </summary>
    /// <param name="state">現在の購読状態.</param>
    /// <returns>表示順に並んだメニュー項目.</returns>
    public static IReadOnlyList<TrayMenuEntry> Build(SubscriptionState state)
    {
        SubscriptionControlAvailability availability = SubscriptionControlAvailability.For(state);

        return
        [
            new(TrayMenuCommand.Open, "開く", IsEnabled: true),
            new(TrayMenuCommand.Start, "購読を開始", availability.CanStart),
            new(TrayMenuCommand.Stop, "購読を停止", availability.CanStop),
            new(TrayMenuCommand.CheckForUpdates, "アプリの更新を確認", IsEnabled: true),
            new(TrayMenuCommand.Separator, string.Empty, IsEnabled: false),
            new(TrayMenuCommand.Exit, "終了", IsEnabled: true),
        ];
    }
}
