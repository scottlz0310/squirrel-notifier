// <copyright file="LauncherArgumentBuilder.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Helpers;

internal static class LauncherArgumentBuilder
{
    private static readonly Regex _safeNameRegex = new(@"^[a-zA-Z0-9_\-\.]+$", RegexOptions.Compiled);

    public static List<string> BuildArguments(string template, ReviewEvent reviewEvent)
    {
        ArgumentNullException.ThrowIfNull(reviewEvent);

        if (string.IsNullOrEmpty(template))
        {
            return new List<string>();
        }

        // Parse arguments structure safely (quotes, backslashes, spaces)
        List<string> rawArgs = McpSubscriptionService.ParseArguments(template);

        string owner = string.Empty;
        string repo = string.Empty;
        if (!string.IsNullOrEmpty(reviewEvent.Repository))
        {
            string[] parts = reviewEvent.Repository.Split('/', 2);
            if (parts.Length == 2)
            {
                owner = parts[0];
                repo = parts[1];
            }
            else
            {
                repo = reviewEvent.Repository;
            }
        }

        // Validate names for security (allow only alphanumeric, hyphen, underscore, dot)
        if (!string.IsNullOrEmpty(owner) && !_safeNameRegex.IsMatch(owner))
        {
            throw new ArgumentException("Invalid owner name in repository field.", nameof(reviewEvent));
        }

        if (!string.IsNullOrEmpty(repo) && !_safeNameRegex.IsMatch(repo))
        {
            throw new ArgumentException("Invalid repository name.", nameof(reviewEvent));
        }

        if (!string.IsNullOrEmpty(reviewEvent.Reason) && !_safeNameRegex.IsMatch(reviewEvent.Reason))
        {
            throw new ArgumentException("Invalid reason value.", nameof(reviewEvent));
        }

        // Validate PR Url
        if (!UrlValidator.IsSafeGitHubUrl(reviewEvent.PrUrl, reviewEvent.Repository, reviewEvent.PrNumber))
        {
            throw new ArgumentException("Invalid PR URL.", nameof(reviewEvent));
        }

        List<string> result = new();
        foreach (string arg in rawArgs)
        {
            string replaced = arg
                .Replace("{owner}", owner, StringComparison.Ordinal)
                .Replace("{repo}", repo, StringComparison.Ordinal)
                .Replace("{prNumber}", reviewEvent.PrNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{prUrl}", reviewEvent.PrUrl, StringComparison.Ordinal)
                .Replace("{reason}", reviewEvent.Reason, StringComparison.Ordinal);
            result.Add(replaced);
        }

        return result;
    }
}
