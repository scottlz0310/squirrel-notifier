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

    internal static IReadOnlyList<string> ParseGatewayUrls(string dockerPsOutput, string mcpRoute = DefaultMcpRoute)
    {
        var candidates = new List<string>();
        foreach (string line in dockerPsOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Match match = _portMappingRegex.Match(line);
            if (match.Success)
            {
                candidates.Add($"http://localhost:{match.Groups[1].Value}{mcpRoute}");
            }
        }

        return candidates;
    }
}
