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

    /// <summary>
    /// http / https の absolute URI かどうかを判定する（#183）。悪意ある／侵害された gateway が
    /// device flow の <c>verification_uri</c> に <c>ms-settings:</c> 等の任意 scheme を返し、
    /// <c>UseShellExecute=true</c> の起動で OS protocol handler を誤起動させることを防ぐため、
    /// 自動でブラウザを開く前にこの許可判定を通す.
    /// </summary>
    /// <param name="url">判定対象の URL.</param>
    /// <returns>http / https の absolute URI の場合は <see langword="true"/>.</returns>
    public static bool IsHttpOrHttpsAbsoluteUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    public static bool IsSafeGitHubUrl(string? url, string repository, int prNumber)
    {
        if (!IsSafeGitHubUrl(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        string expectedPath = $"/{repository}/pull/{prNumber}".ToUpperInvariant();
        string actualPath = uri.AbsolutePath.TrimEnd('/').ToUpperInvariant();

        return expectedPath == actualPath;
    }
}
