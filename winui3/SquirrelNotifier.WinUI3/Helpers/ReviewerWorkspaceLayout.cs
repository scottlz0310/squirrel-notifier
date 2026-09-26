// <copyright file="ReviewerWorkspaceLayout.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// reviewer の PR 単位の作業領域と、reviewer プロセスへ渡す一時領域の環境変数を決める（#403）.
/// </summary>
internal static class ReviewerWorkspaceLayout
{
    /// <summary>
    /// reviewer skill が clone などの一時ファイルを置く場所として参照する環境変数名.
    /// </summary>
    public const string ScratchDirectoryEnvironmentVariable = "SQUIRREL_REVIEW_SCRATCH_DIR";

    private const string _scratchDirectoryName = "tmp";

    public static string GetReviewerRoot(string settingsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsDirectory);
        return Path.Combine(settingsDirectory, "launcher-workspace", "reviewer");
    }

    // owner-repo-pr のように 1 階層へ連結すると、名前に '-' を含む repository 同士
    // （a-b/c と a/b-c）が衝突するため、owner・repo・PR 番号で階層を分ける。
    // GitHub の owner / repo 名は大文字小文字を区別しないため小文字へ正規化し、同じ PR の
    // re-review で作業ディレクトリが一致するようにする（session 再開の条件）.
    [SuppressMessage("Globalization", "CA1308", Justification = "セグメントは ASCII に限定済みで、表示用のパス名として小文字へ正規化する")]
    public static string GetWorkspaceDirectory(string reviewerRoot, string repository, int prNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewerRoot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(prNumber);

        string[] parts = (repository ?? string.Empty).Split('/');
        if (parts.Length != 2 || !parts.All(IsSafeSegment))
        {
            throw new ArgumentException($"Repository '{repository}' は owner/repo の形式ではありません。", nameof(repository));
        }

        return Path.Combine(
            reviewerRoot,
            parts[0].ToLowerInvariant(),
            parts[1].ToLowerInvariant(),
            prNumber.ToString(CultureInfo.InvariantCulture));
    }

    // 最終起動（作業領域ディレクトリの更新時刻）から TTL 以上経ったかを判定する.
    public static bool IsExpired(DateTime lastUsedUtc, DateTimeOffset now, TimeSpan timeToLive)
        => now - new DateTimeOffset(DateTime.SpecifyKind(lastUsedUtc, DateTimeKind.Utc)) >= timeToLive;

    public static string GetScratchDirectory(string workspaceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDirectory);
        return Path.Combine(workspaceDirectory, _scratchDirectoryName);
    }

    // TEMP / TMP の付け替えは skill を変えなくても効く経路、専用の環境変数は付け替えが
    // 効かないエージェントでも skill 側から明示的に参照できる経路として両方渡す.
    public static IReadOnlyDictionary<string, string> BuildEnvironment(string scratchDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchDirectory);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TEMP"] = scratchDirectory,
            ["TMP"] = scratchDirectory,
            [ScratchDirectoryEnvironmentVariable] = scratchDirectory,
        };
    }

    private static bool IsSafeSegment(string segment)
        => !string.IsNullOrWhiteSpace(segment)
            && segment is not "." and not ".."
            && segment.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
