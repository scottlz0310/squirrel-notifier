// <copyright file="RepositoryCheckoutMappingParser.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// Settings UI の <c>owner/repo=絶対パス</c> 形式と永続設定の対応表を相互変換する（#186）.
/// </summary>
internal static class RepositoryCheckoutMappingParser
{
    private static readonly char[] _lineSeparators = ['\r', '\n'];

    public static Dictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in text.Split(_lineSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separatorIndex = line.IndexOf('=', StringComparison.Ordinal);
            if (separatorIndex <= 0 || separatorIndex == line.Length - 1)
            {
                throw new FormatException("Checkout mapping は owner/repo=絶対パス の形式で入力してください。");
            }

            string repository = line[..separatorIndex].Trim();
            string path = line[(separatorIndex + 1)..].Trim();
            if (!IsValidRepository(repository))
            {
                throw new FormatException($"Repository '{repository}' は owner/repo の形式ではありません。");
            }

            if (!Path.IsPathFullyQualified(path))
            {
                throw new FormatException($"Repository '{repository}' の checkout path は絶対パスで指定してください。");
            }

            if (!result.TryAdd(repository, Path.GetFullPath(path)))
            {
                throw new FormatException($"Repository '{repository}' が重複しています。");
            }
        }

        return result;
    }

    public static string Format(IReadOnlyDictionary<string, string> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        return string.Join(Environment.NewLine, mappings
            .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static pair => $"{pair.Key}={pair.Value}"));
    }

    private static bool IsValidRepository(string repository)
    {
        string[] parts = repository.Split('/');
        return parts.Length == 2 && parts.All(static part =>
            !string.IsNullOrWhiteSpace(part)
            && part.All(static c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'));
    }
}
