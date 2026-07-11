// <copyright file="WindowPlacementCalculator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// ライブログウィンドウの初期配置を計算する（#144）。WinUI 型に依存しない純粋計算とし、
/// DPI・マルチモニターは呼び出し側が「現在モニターの work area とウィンドウサイズを
/// 物理ピクセルで渡す」ことで吸収する.
/// </summary>
internal static class WindowPlacementCalculator
{
    /// <summary>
    /// work area 右下へ margin を空けて配置する座標を計算する。ウィンドウが work area より
    /// 大きい場合でも、左上が work area 内に収まるよう clamp し、画面外配置を防ぐ.
    /// </summary>
    /// <param name="workAreaX">work area 左上 X（物理ピクセル）.</param>
    /// <param name="workAreaY">work area 左上 Y（物理ピクセル）.</param>
    /// <param name="workAreaWidth">work area 幅（物理ピクセル）.</param>
    /// <param name="workAreaHeight">work area 高さ（物理ピクセル）.</param>
    /// <param name="windowWidth">ウィンドウ幅（物理ピクセル）.</param>
    /// <param name="windowHeight">ウィンドウ高さ（物理ピクセル）.</param>
    /// <param name="margin">work area 端との余白（物理ピクセル）.</param>
    /// <returns>ウィンドウ左上の配置座標（物理ピクセル）.</returns>
    public static (int X, int Y) CalculateBottomRight(
        int workAreaX,
        int workAreaY,
        int workAreaWidth,
        int workAreaHeight,
        int windowWidth,
        int windowHeight,
        int margin)
    {
        int x = workAreaX + workAreaWidth - windowWidth - margin;
        int y = workAreaY + workAreaHeight - windowHeight - margin;

        // ウィンドウが work area より大きい場合は右下基準を諦め、左上を work area 内へ収める
        return (Math.Max(x, workAreaX), Math.Max(y, workAreaY));
    }
}
