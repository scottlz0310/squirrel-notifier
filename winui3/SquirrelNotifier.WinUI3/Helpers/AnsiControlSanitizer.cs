// <copyright file="AnsiControlSanitizer.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text.RegularExpressions;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// エージェント出力のログ 1 行から ANSI エスケープシーケンスと制御文字を除去する（#144）。
/// CLI エージェントは色付け・カーソル制御のためのエスケープを出力することがあり、
/// そのまま UI へ流すと表示崩れや制御文字インジェクションの原因になる.
/// </summary>
internal static class AnsiControlSanitizer
{
    // CSI（ESC [ ... 終端バイト）、OSC（ESC ] ... BEL / ST。未終端も行末まで）、
    // その他の Fe シーケンス（ESC + 1 文字）を除去する
    private static readonly Regex _ansiEscapeRegex = new(
        @"\x1B(?:\[[^@-~]*[@-~]?|\][^\x07\x1B]*(?:\x07|\x1B\\)?|[@-Z\\-_])?",
        RegexOptions.Compiled);

    // 水平タブ（0x09）を除く C0 制御文字と DEL を除去する
    private static readonly Regex _controlCharRegex = new(
        @"[\x00-\x08\x0A-\x1F\x7F]",
        RegexOptions.Compiled);

    /// <summary>
    /// ANSI エスケープシーケンスと制御文字を除去する。水平タブ（\t）のみ整形用として保持する.
    /// </summary>
    /// <param name="line">ログ 1 行.</param>
    /// <returns>除去後の文字列.</returns>
    public static string Sanitize(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return string.Empty;
        }

        string withoutEscapes = _ansiEscapeRegex.Replace(line, string.Empty);
        return _controlCharRegex.Replace(withoutEscapes, string.Empty);
    }
}
