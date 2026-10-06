// <copyright file="AgyAutoPausePolicy.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>agy のモデルに対応する Auto-Pause 対象枠を判定する.</summary>
internal static class AgyAutoPausePolicy
{
    public static bool IncludesLimit(string? model, string limitId)
        => GetLimitPrefix(model) is not string prefix || limitId.StartsWith(prefix, StringComparison.Ordinal);

    public static string? GetLimitPrefix(string? model)
    {
        if (IsModelFamily(model, "gemini"))
        {
            return "gemini-";
        }

        if (IsModelFamily(model, "claude") || IsModelFamily(model, "gpt"))
        {
            return "3p-";
        }

        return null;
    }

    private static bool IsModelFamily(string? model, string family)
        => model is not null && model.StartsWith(family, StringComparison.OrdinalIgnoreCase)
            && (model.Length == family.Length || model[family.Length] is '-' or ' ');
}
