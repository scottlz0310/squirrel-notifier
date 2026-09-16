// <copyright file="RateLimitDisplayName.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// レートリミット枠の表示名を組み立てる（#335）。枠のラベルだけではどのサービスの枠か
/// 判別できないため、エージェント由来の枠にはサービス名を前置する.
/// </summary>
internal static class RateLimitDisplayName
{
    private const string _agentUriPrefix = "agent://";
    private const string _separator = " — ";

    /// <summary>エージェント ID をカタログの表示名（例: <c>Codex</c>）へ解決する.</summary>
    /// <param name="agentId">エージェント ID.</param>
    /// <returns>カタログに無い ID はそのまま返す.</returns>
    public static string ResolveAgentDisplayName(string? agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return string.Empty;
        }

        return RateLimitAgentCatalog.All.FirstOrDefault(agent => agent.Id == agentId)?.DisplayName ?? agentId;
    }

    /// <summary>サービス名と枠のラベルを連結する.</summary>
    /// <param name="agentDisplayName">エージェントの表示名.</param>
    /// <param name="limitLabel">枠のラベル.</param>
    /// <returns>表示名。<paramref name="agentDisplayName"/> が空なら枠のラベルのみ.</returns>
    public static string Combine(string? agentDisplayName, string limitLabel)
        => string.IsNullOrWhiteSpace(agentDisplayName) ? limitLabel : $"{agentDisplayName}{_separator}{limitLabel}";

    /// <summary>
    /// 枠の出所（<see cref="RateLimitInfo.SourceUri"/>）から表示名を組み立てる。
    /// <c>agent://&lt;id&gt;</c> 由来の枠にはサービス名を前置し、MCP の <c>ratelimit://</c>
    /// リソース由来の枠は URI 自体が出所を示すためラベルをそのまま使う.
    /// </summary>
    /// <param name="sourceUri">枠の出所を示す URI.</param>
    /// <param name="limitLabel">枠のラベル.</param>
    /// <returns>表示名.</returns>
    public static string BuildFromSourceUri(string? sourceUri, string limitLabel)
    {
        if (sourceUri is null || !sourceUri.StartsWith(_agentUriPrefix, StringComparison.Ordinal))
        {
            return limitLabel;
        }

        string agentId = sourceUri[_agentUriPrefix.Length..];
        return Combine(ResolveAgentDisplayName(agentId), limitLabel);
    }
}
