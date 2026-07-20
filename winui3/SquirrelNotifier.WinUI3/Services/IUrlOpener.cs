// <copyright file="IUrlOpener.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// 既定ブラウザ等で URL を開くための抽象。ブラウザ起動失敗を単体テストで再現できるよう
/// <see cref="McpLoginService"/> から DI する（#183）.
/// </summary>
internal interface IUrlOpener
{
    /// <summary>
    /// 指定した URL を既定ブラウザで開く.
    /// </summary>
    /// <param name="url">開く URL.</param>
    /// <returns>起動に成功した場合は <see langword="true"/>、失敗した場合は <see langword="false"/>.</returns>
    bool TryOpen(string url);
}
