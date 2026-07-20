// <copyright file="UrlOpener.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using SquirrelNotifier.WinUI3.Helpers;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// <see cref="IUrlOpener"/> の既定実装。<see cref="Process.Start(ProcessStartInfo)"/> の
/// <c>UseShellExecute</c> で既定ブラウザへ委譲する.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class UrlOpener : IUrlOpener
{
    public bool TryOpen(string url)
    {
        // sink 側の多層防御（#183）: UseShellExecute=true は任意の OS protocol handler を起動しうる
        // ため、http / https の absolute URI 以外は Process.Start へ渡さず拒否する.
        if (!UrlValidator.IsHttpOrHttpsAbsoluteUrl(url))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            // ブラウザ起動失敗（既定ブラウザ未設定・URL 不正等）でも認証処理は継続するため、
            // 例外は握りつぶし false を返す。呼び出し元は URL / code を UI で提示する（#183）。
            return false;
        }
    }
}
