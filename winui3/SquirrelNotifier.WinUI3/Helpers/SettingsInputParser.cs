// <copyright file="SettingsInputParser.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Collections.Generic;
using System.Linq;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>Settings 画面から渡された生の入力値.</summary>
internal sealed record SettingsInput(
    string CommandPath,
    string Arguments,
    string GatewayUrl,
    string ResourceUrisText,
    double NotificationTimeoutValue,
    string ReviewerLauncherCommandPath,
    string ReviewerLauncherArguments,
    string ReviewerLauncherResumeArguments,
    string ReviewedLauncherCommandPath,
    string ReviewedLauncherArguments,
    string ReviewedLauncherResumeArguments,
    double LauncherTimeoutValue,
    bool SessionResumeEnabled,
    string RepositoryCheckoutMappingsText);

/// <summary>SettingsService へ渡せる形まで変換済みの入力値.</summary>
internal sealed record SettingsUpdateValues(
    string CommandPath,
    string Arguments,
    string GatewayUrl,
    IReadOnlyList<string> ResourceUris,
    int NotificationTimeoutMs,
    string ReviewerLauncherCommandPath,
    string ReviewerLauncherArguments,
    string ReviewerLauncherResumeArguments,
    string ReviewedLauncherCommandPath,
    string ReviewedLauncherArguments,
    string ReviewedLauncherResumeArguments,
    int LauncherTimeoutMs,
    bool SessionResumeEnabled,
    string ReviewerPresetId,
    string ReviewedPresetId,
    IReadOnlyDictionary<string, string> RepositoryCheckoutMappings);

/// <summary>
/// Settings UI の生テキストを保存可能な値へ変換する。入力途中の値は null を返し、
/// 呼び出し側が既存設定を保持できるようにする.
/// </summary>
internal static class SettingsInputParser
{
    private static readonly char[] _lineSeparators = ['\r', '\n'];

    public static SettingsUpdateValues? TryParse(SettingsInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        List<string> resourceUris = ParseResourceUris(input.ResourceUrisText);
        if (resourceUris.Count == 0)
        {
            return null;
        }

        if (!TryNormalizeNumber(input.NotificationTimeoutValue, 60000, 1, 300000, out int notificationTimeoutMs)
            || !TryNormalizeNumber(input.LauncherTimeoutValue, 1800000, 1, 7200000, out int launcherTimeoutMs))
        {
            return null;
        }

        Dictionary<string, string> repositoryCheckoutMappings;
        try
        {
            repositoryCheckoutMappings = RepositoryCheckoutMappingParser.Parse(input.RepositoryCheckoutMappingsText);
        }
        catch (FormatException)
        {
            // 入力途中の mapping は保存せず、次の TextChanged で再試行する。
            return null;
        }

        return new SettingsUpdateValues(
            input.CommandPath,
            input.Arguments,
            input.GatewayUrl,
            resourceUris,
            notificationTimeoutMs,
            input.ReviewerLauncherCommandPath,
            input.ReviewerLauncherArguments,
            input.ReviewerLauncherResumeArguments,
            input.ReviewedLauncherCommandPath,
            input.ReviewedLauncherArguments,
            input.ReviewedLauncherResumeArguments,
            launcherTimeoutMs,
            input.SessionResumeEnabled,
            LauncherAgentCatalog.ResolvePresetId(
                input.ReviewerLauncherCommandPath,
                input.ReviewerLauncherArguments,
                input.ReviewerLauncherResumeArguments,
                LauncherRole.Reviewer),
            LauncherAgentCatalog.ResolvePresetId(
                input.ReviewedLauncherCommandPath,
                input.ReviewedLauncherArguments,
                input.ReviewedLauncherResumeArguments,
                LauncherRole.Reviewed),
            repositoryCheckoutMappings);
    }

    public static List<string> ParseResourceUris(string text)
    {
        return text
            .Split(_lineSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct()
            .Where(static uri => !string.IsNullOrWhiteSpace(uri))
            .ToList();
    }

    public static string MergeResourceUris(string existingText, IEnumerable<string> resourceUris)
    {
        List<string> merged = ParseResourceUris(existingText);
        var seen = merged.ToHashSet(StringComparer.Ordinal);

        foreach (string resourceUri in resourceUris)
        {
            if (!string.IsNullOrWhiteSpace(resourceUri) && seen.Add(resourceUri))
            {
                merged.Add(resourceUri);
            }
        }

        return string.Join("\n", merged);
    }

    private static bool TryNormalizeNumber(double value, int nanFallback, int minimum, int maximum, out int result)
    {
        if (double.IsNaN(value))
        {
            result = nanFallback;
            return true;
        }

        if (!double.IsFinite(value) || value < int.MinValue || value > int.MaxValue)
        {
            result = default;
            return false;
        }

        result = (int)value;
        return result >= minimum && result <= maximum;
    }
}
