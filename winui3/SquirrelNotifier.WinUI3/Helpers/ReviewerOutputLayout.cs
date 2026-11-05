// <copyright file="ReviewerOutputLayout.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// 保持対象になりうる reviewer 出力ファイル 1 件の、削除判定に必要な属性.
/// </summary>
/// <param name="Path">ファイルのパス.</param>
/// <param name="LastWriteUtc">最終更新時刻（UTC）.</param>
/// <param name="Length">サイズ（バイト）.</param>
internal sealed record ReviewerOutputFile(string Path, DateTime LastWriteUtc, long Length);

/// <summary>
/// reviewer プロセスの生の標準出力・標準エラーを記録するファイルの置き場所と、保持ルールを決める.
/// レビューが停止・異常終了したときに、実行ウィンドウが閉じた後でも原因を追えるようにするための記録で、
/// 作業領域（<see cref="ReviewerWorkspaceLayout"/>）の片付けとは別のルートに置く.
/// </summary>
internal static class ReviewerOutputLayout
{
    public static string GetRoot(string settingsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsDirectory);
        return Path.Combine(settingsDirectory, "reviewer-output");
    }

    // PR ごとに階層を分け、ファイル名は試行の開始時刻（ミリ秒まで）にする。再レビューや自動再開で
    // 同じ PR の試行が続いても、名前の昇順が実行順になり、上書きされない.
    public static string GetFilePath(string root, string repository, int prNumber, DateTimeOffset startedAt)
    {
        string directory = ReviewerWorkspaceLayout.GetWorkspaceDirectory(root, repository, prNumber);
        string name = startedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'.'fff'Z'", CultureInfo.InvariantCulture) + ".log";
        return Path.Combine(directory, name);
    }

    // 保持期間を過ぎたものを削除し、残りの合計が上限を超える場合は古いものから削除する.
    public static IReadOnlyList<string> SelectDeletions(
        IEnumerable<ReviewerOutputFile> files,
        DateTimeOffset now,
        TimeSpan retention,
        long maxTotalBytes)
    {
        ArgumentNullException.ThrowIfNull(files);

        var deletions = new List<string>();
        long total = 0;
        foreach (ReviewerOutputFile file in files.OrderByDescending(static f => f.LastWriteUtc))
        {
            DateTimeOffset lastWrite = new(DateTime.SpecifyKind(file.LastWriteUtc, DateTimeKind.Utc));
            if (now - lastWrite >= retention || total + file.Length > maxTotalBytes)
            {
                deletions.Add(file.Path);
                continue;
            }

            total += file.Length;
        }

        return deletions;
    }
}
