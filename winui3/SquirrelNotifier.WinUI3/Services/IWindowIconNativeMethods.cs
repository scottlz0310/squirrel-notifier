// <copyright file="IWindowIconNativeMethods.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// ウィンドウアイコン設定に必要な OS 境界を抽象化する。.
/// </summary>
internal interface IWindowIconNativeMethods
{
    /// <summary>
    /// 指定したサイズのアイコンハンドルを読み込む。.
    /// </summary>
    /// <param name="iconPath">アイコンファイルのパス。.</param>
    /// <param name="pixelSize">読み込むアイコンの一辺のピクセル数。.</param>
    /// <returns>読み込んだアイコンハンドル。失敗時はゼロ。.</returns>
    nint LoadIcon(string iconPath, int pixelSize);

    /// <summary>
    /// 読み込んだアイコンをウィンドウへ設定する。.
    /// </summary>
    /// <param name="windowHandle">設定対象のウィンドウハンドル。.</param>
    /// <param name="iconSize">設定するアイコンのサイズ種別。.</param>
    /// <param name="iconHandle">設定するアイコンハンドル。.</param>
    void SetIcon(nint windowHandle, WindowIconSize iconSize, nint iconHandle);
}

/// <summary>
/// ウィンドウに設定するアイコンのサイズ種別。.
/// </summary>
internal enum WindowIconSize
{
    /// <summary>小さいアイコン。.</summary>
    Small,

    /// <summary>大きいアイコン。.</summary>
    Large,
}
