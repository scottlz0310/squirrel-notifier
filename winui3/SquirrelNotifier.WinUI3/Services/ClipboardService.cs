// <copyright file="ClipboardService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using Windows.ApplicationModel.DataTransfer;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// Windows のクリップボードへテキストを設定する.
/// </summary>
internal sealed class ClipboardService : IClipboardService
{
    private readonly Action<string> _setText;

    public ClipboardService()
        : this(SetTextOnWindowsClipboard)
    {
    }

    internal ClipboardService(Action<string> setText)
    {
        ArgumentNullException.ThrowIfNull(setText);
        _setText = setText;
    }

    public void SetText(string text)
    {
        _setText(text);
    }

    private static void SetTextOnWindowsClipboard(string text)
    {
        DataPackage dataPackage = new();
        dataPackage.SetText(text);
        Clipboard.SetContent(dataPackage);
    }
}
