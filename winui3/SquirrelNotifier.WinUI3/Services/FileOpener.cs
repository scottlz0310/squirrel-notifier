// <copyright file="FileOpener.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// <see cref="IFileOpener"/> の既定実装。<see cref="Process.Start(ProcessStartInfo)"/> の
/// <c>UseShellExecute</c> でファイルまたはフォルダーを既定のアプリへ委譲する.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class FileOpener : IFileOpener
{
    public bool TryOpen(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
