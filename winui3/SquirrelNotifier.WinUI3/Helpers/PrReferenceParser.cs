// <copyright file="PrReferenceParser.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Globalization;
using System.Text.RegularExpressions;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// GitHub PR URL または <c>owner/repo#number</c> 形式の入力を (owner, repo, prNumber) にパースする。.
/// </summary>
internal static class PrReferenceParser
{
    // 末尾の /files, /commits や ?diff=..., #discussion_r... のような、コピー&ペーストされがちな
    // 付随パス・クエリ・フラグメントを許容するため、番号直後の区切り文字以降は無視する。
    private static readonly Regex _urlPattern = new(
        @"^https://github\.com/(?<owner>[A-Za-z0-9][A-Za-z0-9\-]*)/(?<repo>[A-Za-z0-9_.\-]+)/pull/(?<number>[1-9][0-9]*)(?:[/?#].*)?$",
        RegexOptions.Compiled);

    private static readonly Regex _shorthandPattern = new(
        @"^(?<owner>[A-Za-z0-9][A-Za-z0-9\-]*)/(?<repo>[A-Za-z0-9_.\-]+)#(?<number>[1-9][0-9]*)$",
        RegexOptions.Compiled);

    public static bool TryParse(string? input, out PrReference? reference)
    {
        reference = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string trimmed = input.Trim();

        Match match = _urlPattern.Match(trimmed);
        if (!match.Success)
        {
            match = _shorthandPattern.Match(trimmed);
        }

        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int prNumber))
        {
            return false;
        }

        reference = new PrReference(match.Groups["owner"].Value, match.Groups["repo"].Value, prNumber);
        return true;
    }
}
