// <copyright file="LaunchWindowPolicy.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using Microsoft.Windows.AppLifecycle;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>起動のされ方から、ウィンドウを表示するかを判定する.</summary>
internal static class LaunchWindowPolicy
{
    /// <summary>起動時にウィンドウを表示するかを判定する.</summary>
    /// <param name="commandLineArgs">プロセスのコマンドライン引数.</param>
    /// <param name="activationKind">このプロセスのアクティブ化の種別.</param>
    /// <returns>表示する場合は <see langword="true"/>。<see langword="false"/> はトレイへの常駐.</returns>
    public static bool ShouldShowWindow(IReadOnlyCollection<string> commandLineArgs, ExtendedActivationKind activationKind)
    {
        ArgumentNullException.ThrowIfNull(commandLineArgs);

        // スタートアップタスクには引数を指定していないため、MSIX 版の自動起動は種別で判定する（#569）
        return activationKind != ExtendedActivationKind.StartupTask
            && !commandLineArgs.Contains("--tray")
            && !commandLineArgs.Contains("-t");
    }

    /// <summary>起動のされ方と判定結果を、ログの 1 行にする.</summary>
    /// <param name="activationKind">このプロセスのアクティブ化の種別.</param>
    /// <param name="showWindow">ウィンドウを表示するか.</param>
    /// <returns>ログに書く文字列.</returns>
    public static string DescribeLaunch(ExtendedActivationKind activationKind, bool showWindow)
        => $"[INFO] 起動種別: {activationKind}、ウィンドウ: {(showWindow ? "表示" : "トレイに常駐")}";

    /// <summary>二重起動から転送されたアクティブ化で、既存のウィンドウを前面化するかを判定する.</summary>
    /// <param name="activationKind">転送されたアクティブ化の種別.</param>
    /// <returns>前面化する場合は <see langword="true"/>.</returns>
    public static bool ShouldBringToFront(ExtendedActivationKind activationKind)
        => activationKind != ExtendedActivationKind.StartupTask;
}
