// <copyright file="ProcessOutputSummarizer.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Linq;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// 子プロセスの stderr / stdout を、ログ・UI へ出す前に安全な短い 1 行へ要約する（#201）。
/// ANSI 制御列を除去し <see cref="SecretMasker"/> を通したうえで、先頭数行・一定長へ丸める.
/// </summary>
internal static class ProcessOutputSummarizer
{
    private const int _maxLines = 3;
    private const int _maxLength = 500;

    /// <summary>
    /// 出力テキストを要約する.
    /// </summary>
    /// <param name="text">子プロセスの出力（複数行可）.</param>
    /// <param name="masker">機密値のマスカー.</param>
    /// <returns>要約済みの 1 行。<paramref name="text"/> が空なら空文字.</returns>
    public static string Summarize(string text, SecretMasker masker)
    {
        ArgumentNullException.ThrowIfNull(masker);

        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string summary = string.Join(" | ", text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(_maxLines)
            .Select(line => masker.Mask(AnsiControlSanitizer.Sanitize(line))));

        return summary.Length <= _maxLength ? summary : summary[.._maxLength];
    }
}
