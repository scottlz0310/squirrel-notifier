// <copyright file="ReviewObservationBuilder.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Globalization;
using System.Text.RegularExpressions;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// PR ごとのレビューの観測から、review-raven へ送る観測（<c>publish_review_observation</c> の入力）を組み立てる.
/// 状態を持たず、入力と出力だけで決まる。起動待ちの理由は、公開状態（<c>review-status.json</c>）と同じ規則で決める.
/// </summary>
internal static class ReviewObservationBuilder
{
    /// <summary>契約の版.</summary>
    internal const int SchemaVersion = 1;

    // 契約の producerId の形式
    private static readonly Regex _producerIdPattern = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>送信主体の ID が契約の形式か.</summary>
    /// <param name="producerId">送信主体の ID.</param>
    /// <returns>契約の形式なら <see langword="true"/>.</returns>
    public static bool IsValidProducerId(string? producerId)
        => producerId is not null && _producerIdPattern.IsMatch(producerId);

    /// <summary>
    /// 観測を組み立てる。review-raven の依頼 cycle との対応が無い項目（旧経路の候補）は、送り先の cycle が無いため
    /// <see langword="null"/> を返す.
    /// </summary>
    /// <param name="entry">PR ごとのレビューの観測.</param>
    /// <param name="autoStartEnabled">「レビュー自動開始」が on か.</param>
    /// <param name="producerId">送信主体の ID（契約の形式）.</param>
    /// <param name="producerEpoch">送信主体の世代（1 以上）.</param>
    /// <param name="sequence">同じ cycle・世代の中の連番（0 以上）.</param>
    /// <param name="observedAt">観測した時刻.</param>
    /// <param name="stopped">送信主体が止まることを知らせるか.</param>
    /// <returns>観測。cycle との対応が無ければ <see langword="null"/>.</returns>
    public static ReviewObservation? Build(
        ReviewStatusEntry entry,
        bool autoStartEnabled,
        string producerId,
        long producerEpoch,
        long sequence,
        DateTimeOffset observedAt,
        bool stopped = false)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!IsValidProducerId(producerId))
        {
            throw new ArgumentException("producerId が契約の形式ではありません。", nameof(producerId));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(producerEpoch, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);

        if (entry.Cycle is not { } cycle)
        {
            return null;
        }

        bool finished = entry.State == ReviewStatusState.Finished;
        return new ReviewObservation(
            SchemaVersion,
            cycle.CycleId,
            cycle.CandidateId,
            cycle.HeadSha,
            producerId,
            producerEpoch,
            sequence,
            observedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ReviewStatusDocumentBuilder.DescribeState(entry.State),
            ReviewStatusDocumentBuilder.DescribeHoldReason(entry, autoStartEnabled),
            finished && entry.Outcome is ReviewerOutcome outcome ? ReviewStatusDocumentBuilder.DescribeOutcome(outcome) : null,
            finished ? entry.ExitCode : null,
            stopped);
    }
}
