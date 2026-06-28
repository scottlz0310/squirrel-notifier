// <copyright file="DockerPortParser.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text.RegularExpressions;

namespace SquirrelNotifier.WinUI3.Helpers;

internal static class DockerPortParser
{
    // squirrel-notifier が購読する mcp-gateway の既定 route パス
    internal const string DefaultMcpRoute = "/mcp/thread-owl";

    private static readonly Regex _portMappingRegex = new(@":(\d+)->\d+/tcp", RegexOptions.Compiled);

    // docker ps のポートマッピングから route を含まない base URL（http://localhost:PORT）の一覧を返す。
    // route は呼び出し側で CombineRoute を用いて付与する。
    internal static IReadOnlyList<string> ParseGatewayBaseUrls(string dockerPsOutput)
    {
        var candidates = new List<string>();
        foreach (string line in dockerPsOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Match match = _portMappingRegex.Match(line);
            if (match.Success)
            {
                candidates.Add($"http://localhost:{match.Groups[1].Value}");
            }
        }

        return candidates;
    }

    // base URL に MCP route パスを正規化して結合する。
    // route が空の場合は base をそのまま返す。先頭スラッシュの補完・余分なスラッシュの除去を行う。
    internal static string CombineRoute(string baseUrl, string route)
    {
        string trimmedBase = baseUrl.TrimEnd('/');
        string trimmedRoute = (route ?? string.Empty).Trim();
        if (trimmedRoute.Length == 0)
        {
            return trimmedBase;
        }

        if (!trimmedRoute.StartsWith('/'))
        {
            trimmedRoute = "/" + trimmedRoute;
        }

        trimmedRoute = trimmedRoute.TrimEnd('/');
        return trimmedRoute.Length == 0 ? trimmedBase : trimmedBase + trimmedRoute;
    }

    // docker ps のポートマッピングから route を付与した Gateway URL の一覧を返す。
    internal static IReadOnlyList<string> ParseGatewayUrls(string dockerPsOutput, string mcpRoute = DefaultMcpRoute)
    {
        var result = new List<string>();
        foreach (string baseUrl in ParseGatewayBaseUrls(dockerPsOutput))
        {
            result.Add(CombineRoute(baseUrl, mcpRoute));
        }

        return result;
    }
}
