// <copyright file="IWindowIconService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// ウィンドウアイコン設定の境界を表す。.
/// </summary>
internal interface IWindowIconService
{
    /// <summary>
    /// 指定したウィンドウへアプリケーションアイコンを設定する。.
    /// </summary>
    /// <param name="windowHandle">アイコンを設定するウィンドウのハンドル。.</param>
    /// <returns>少なくとも一つのアイコンを設定できた場合は <see langword="true"/>。.</returns>
    bool TrySetIcon(nint windowHandle);

    /// <summary>
    /// <see cref="TrySetIcon"/> で読み込んだアイコンハンドルをすべて解放する。.
    /// </summary>
    /// <remarks>
    /// アイコンを適用中のウィンドウに対しては呼び出さない。ウィンドウ close 後に呼び出す。.
    /// </remarks>
    void ReleaseIcons();
}
