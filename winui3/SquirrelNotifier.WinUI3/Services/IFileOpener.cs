// <copyright file="IFileOpener.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// ファイルシステムのパスを既定のシェルで開くための抽象.
/// </summary>
internal interface IFileOpener
{
    /// <summary>
    /// 指定したパスを既定のシェルで開く.
    /// </summary>
    /// <param name="path">開くファイルまたはフォルダーのパス.</param>
    /// <returns>起動に成功した場合は <see langword="true"/>、失敗した場合は <see langword="false"/>.</returns>
    bool TryOpen(string path);
}
