// <copyright file="LauncherSessionConflictPolicy.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text.RegularExpressions;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>Codex が対象セッションの永続化初期化で失敗した場合だけを識別する.</summary>
internal static partial class LauncherSessionConflictPolicy
{
    public static bool IsStartupConflict(string stdout, string stderr, Guid sessionId)
    {
        // 出力が始まった実行は副作用の有無を判断できないため、新規起動へ戻さない。
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            return false;
        }

        Match match = StartupConflictRegex().Match(stderr);
        return match.Success
            && Guid.TryParse(match.Groups["session"].Value, out Guid failedSession)
            && failedSession == sessionId;
    }

    [GeneratedRegex(@"failed to initialize thread persistence:\s*thread-store conflict:\s*thread (?<session>[0-9a-fA-F-]{36}) already has an active writer", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex StartupConflictRegex();
}
