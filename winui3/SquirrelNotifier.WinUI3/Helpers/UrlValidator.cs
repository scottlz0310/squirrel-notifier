// <copyright file="UrlValidator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Text.RegularExpressions;

namespace SquirrelNotifier.WinUI3.Helpers;

internal static class UrlValidator
{
    private static readonly Regex _safePathRegex = new(@"^/[a-zA-Z0-9_\-\./]+(?:\?[a-zA-Z0-9_\-\.&%=]+)?$", RegexOptions.Compiled);

    public static bool IsSafeGitHubUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (uri.Host != "github.com")
        {
            return false;
        }

        string pathAndQuery = uri.PathAndQuery;
        return _safePathRegex.IsMatch(pathAndQuery);
    }
}
