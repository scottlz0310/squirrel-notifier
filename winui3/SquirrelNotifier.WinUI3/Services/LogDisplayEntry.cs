// <copyright file="LogDisplayEntry.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// 同じ文言の行も、ListView では別の表示項目として保持する.
/// </summary>
internal sealed class LogDisplayEntry
{
    public LogDisplayEntry(string text)
    {
        Text = text;
    }

    public string Text { get; }
}
