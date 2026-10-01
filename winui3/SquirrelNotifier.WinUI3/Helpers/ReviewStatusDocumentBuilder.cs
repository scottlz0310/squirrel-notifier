// <copyright file="ReviewStatusDocumentBuilder.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// 観測した PR ごとのレビューの状態から、公開状態（<c>review-status.json</c>）の文書を組み立てる（#462）.
/// 状態を持たず、入力と出力だけで決まる.
/// </summary>
internal static class ReviewStatusDocumentBuilder
{
    internal const int SchemaVersion = 1;

    /// <summary>reviewer を同時に実行できる件数。並列化（D8）までは 1 で固定する.</summary>
    internal const int MaxConcurrent = 1;

    /// <summary><c>recent[]</c> に残す件数の上限.</summary>
    internal const int RecentLimit = 50;

    /// <summary><c>recent[]</c> に残す期間.</summary>
    internal static readonly TimeSpan RecentRetention = TimeSpan.FromHours(24);

    /// <summary>
    /// 文書を組み立てる。<c>items[]</c> は実行中、起動待ちの順（起動待ちは保留し始めた順）、
    /// <c>recent[]</c> は終了の新しい順で、期間と件数の上限を超えたものを含めない.
    /// </summary>
    /// <param name="entries">PR ごとのレビューの観測（起動待ち・実行中・終了）.</param>
    /// <param name="environment">アプリ全体の観測.</param>
    /// <param name="now">書き出す時刻.</param>
    /// <returns>公開状態の文書.</returns>
    public static ReviewStatusDocument Build(
        IEnumerable<ReviewStatusEntry> entries,
        ReviewStatusEnvironment environment,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(environment);

        ReviewStatusEntry[] all = [.. entries];

        ReviewStatusEntry[] running = [.. all
            .Where(static entry => entry.State == ReviewStatusState.Running)
            .OrderBy(static entry => entry.StartedAt ?? entry.ReceivedAt)
            .ThenBy(static entry => entry.Key, StringComparer.Ordinal)];

        // 保留を観測した順に待ち順を付ける。保留を観測していない起動待ち（手動運用、または評価の途中）は、待ち順を持たない
        ReviewStatusEntry[] waiting = [.. all
            .Where(static entry => entry.State == ReviewStatusState.Waiting)
            .OrderBy(static entry => entry.HoldSince ?? entry.ReceivedAt)
            .ThenBy(static entry => entry.ReceivedAt)
            .ThenBy(static entry => entry.Key, StringComparer.Ordinal)];

        List<ReviewStatusItem> items = [];
        items.AddRange(running.Select(entry => ToItem(entry, environment, queuePosition: null)));

        int position = 0;
        foreach (ReviewStatusEntry entry in waiting)
        {
            int? queuePosition = entry.Hold is null ? null : ++position;
            items.Add(ToItem(entry, environment, queuePosition));
        }

        DateTimeOffset cutoff = now - RecentRetention;
        ReviewStatusItem[] recent = [.. all
            .Where(entry => entry.State == ReviewStatusState.Finished && entry.FinishedAt >= cutoff)
            .OrderByDescending(static entry => entry.FinishedAt)
            .ThenBy(static entry => entry.Key, StringComparer.Ordinal)
            .Take(RecentLimit)
            .Select(entry => ToItem(entry, environment, queuePosition: null))];

        return new ReviewStatusDocument(
            SchemaVersion,
            now.UtcDateTime,
            new ReviewStatusApp(environment.AppVersion, environment.AppStartedAt.UtcDateTime),
            new ReviewStatusConcurrency(running.Length, MaxConcurrent),
            new ReviewStatusSubscription(environment.SubscriptionState, environment.SubscriptionSince.UtcDateTime),
            items,
            recent);
    }

    /// <summary>PR の公開状態の照合用の key（小文字の <c>owner/repo#N</c>）を作る.</summary>
    /// <param name="repository"><c>owner/repo</c>.</param>
    /// <param name="prNumber">PR 番号.</param>
    /// <returns>key.</returns>
    [SuppressMessage("Globalization", "CA1308", Justification = "公開契約の key は、thread-owl の review://status の URI（小文字へ正規化される）と照合するため、小文字で定める")]
    public static string CreateKey(string repository, int prNumber)
        => $"{repository.Trim().ToLowerInvariant()}#{prNumber}";

    private static ReviewStatusItem ToItem(
        ReviewStatusEntry entry,
        ReviewStatusEnvironment environment,
        int? queuePosition)
    {
        // 保留を観測していない起動待ちのうち、自動起動が off のものは、操作（「レビューする」）を待っている
        string? holdReason = entry.State != ReviewStatusState.Waiting
            ? null
            : entry.Hold is ReviewHoldKind hold
                ? DescribeHold(hold)
                : environment.AutoStartEnabled ? null : "manual";
        DateTimeOffset? holdSince = holdReason is null ? null : entry.HoldSince ?? entry.ReceivedAt;

        return new ReviewStatusItem(
            entry.Key,
            entry.Repository,
            entry.PrNumber,
            entry.Round,
            entry.EventId,
            entry.Reason,
            DescribeState(entry.State),
            entry.ReceivedAt.UtcDateTime,
            entry.StartedAt?.UtcDateTime,
            entry.FinishedAt?.UtcDateTime,
            entry.Agent,
            holdReason,
            holdSince?.UtcDateTime,
            queuePosition,
            entry.Outcome is ReviewerOutcome outcome ? DescribeOutcome(outcome) : null,
            entry.ExitCode);
    }

    private static string DescribeState(ReviewStatusState state)
        => state switch
        {
            ReviewStatusState.Waiting => "waiting",
            ReviewStatusState.Running => "running",
            ReviewStatusState.Finished => "finished",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "未知のレビューの段階です。"),
        };

    private static string DescribeHold(ReviewHoldKind hold)
        => hold switch
        {
            ReviewHoldKind.Busy => "busy",
            ReviewHoldKind.CiPending => "ciPending",
            ReviewHoldKind.AutoPause => "autoPause",
            _ => throw new ArgumentOutOfRangeException(nameof(hold), hold, "未知の保留の理由です。"),
        };

    private static string DescribeOutcome(ReviewerOutcome outcome)
        => outcome switch
        {
            ReviewerOutcome.Completed => "completed",
            ReviewerOutcome.Failed => "failed",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "未知の終了結果です。"),
        };
}
