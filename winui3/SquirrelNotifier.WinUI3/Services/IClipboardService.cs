// <copyright file="IClipboardService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// テキストを OS のクリップボードへ設定する境界.
/// </summary>
internal interface IClipboardService
{
    /// <summary>
    /// 指定したテキストをクリップボードへ設定する.
    /// </summary>
    /// <param name="text">設定するテキスト.</param>
    void SetText(string text);
}
