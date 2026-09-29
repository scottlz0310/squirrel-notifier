// <copyright file="PendingReviewStartQueue.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary><see cref="PendingReviewStartQueue.AddOrReplace"/> の結果（#339）.</summary>
internal enum PendingReviewStartChange
{
    /// <summary>保留していない PR のイベントを末尾に追加した.</summary>
    Added,

    /// <summary>保留中の同じ PR のイベントを、順番を保ったまま置き換えた.</summary>
    Replaced,

    /// <summary>保留中のイベントのほうが新しいため、何も変えなかった.</summary>
    Ignored,
}

/// <summary>
/// 別のレビューが実行中のため自動起動を見送ったレビューイベントを、実行終了後の再評価まで保留する（#339）。
/// PR 単位で 1 件だけ保持し、受信順を保つ.
/// </summary>
/// <remarks>スレッドセーフではない。UI スレッドからの利用を前提とする.</remarks>
internal sealed class PendingReviewStartQueue
{
    private readonly List<ReviewEvent> _events = [];

    public int Count => _events.Count;

    /// <summary>
    /// イベントを保留する。同じ PR のイベントを保留中なら、受信が新しいほうを同じ位置に残す.
    /// </summary>
    /// <param name="reviewEvent">保留するイベント.</param>
    /// <returns>保留状態の変化.</returns>
    public PendingReviewStartChange AddOrReplace(ReviewEvent reviewEvent)
    {
        ArgumentNullException.ThrowIfNull(reviewEvent);

        int index = FindPullRequestIndex(reviewEvent);
        if (index < 0)
        {
            _events.Add(reviewEvent);
            return PendingReviewStartChange.Added;
        }

        // 再評価の await 中に同じ PR の新しいイベントが届いた場合、再評価側が古いイベントを
        // 保留へ戻しても新しいイベントを上書きしないよう、受信時刻で比較する
        if (ReferenceEquals(_events[index], reviewEvent) || reviewEvent.ReceivedTime < _events[index].ReceivedTime)
        {
            return PendingReviewStartChange.Ignored;
        }

        _events[index] = reviewEvent;
        return PendingReviewStartChange.Replaced;
    }

    /// <summary>
    /// 保留中のイベントを、保留した順に返す。返した一覧は、その後の保留の変化に影響されない.
    /// </summary>
    /// <returns>保留中のイベントの一覧。無い場合は空.</returns>
    public IReadOnlyList<ReviewEvent> Snapshot() => [.. _events];

    /// <summary>指定したイベントそのものを保留しているか.</summary>
    /// <param name="reviewEvent">確認するイベント.</param>
    /// <returns>保留している場合は <see langword="true"/>.</returns>
    public bool Contains(ReviewEvent reviewEvent)
    {
        ArgumentNullException.ThrowIfNull(reviewEvent);
        return _events.Contains(reviewEvent);
    }

    /// <summary>指定したイベントそのものを保留している場合だけ外す.</summary>
    /// <param name="reviewEvent">外すイベント.</param>
    /// <returns>外した場合は <see langword="true"/>.</returns>
    public bool Remove(ReviewEvent reviewEvent)
    {
        ArgumentNullException.ThrowIfNull(reviewEvent);
        return _events.Remove(reviewEvent);
    }

    /// <summary>指定したイベントと同じ PR の保留を、どのイベントかに関わらず外す.</summary>
    /// <param name="reviewEvent">対象 PR のイベント.</param>
    /// <returns>外した場合は <see langword="true"/>.</returns>
    public bool RemovePullRequest(ReviewEvent reviewEvent)
    {
        ArgumentNullException.ThrowIfNull(reviewEvent);

        int index = FindPullRequestIndex(reviewEvent);
        if (index < 0)
        {
            return false;
        }

        _events.RemoveAt(index);
        return true;
    }

    private int FindPullRequestIndex(ReviewEvent reviewEvent)
        => _events.FindIndex(pending =>
            pending.PrNumber == reviewEvent.PrNumber
            && string.Equals(pending.Repository, reviewEvent.Repository, StringComparison.OrdinalIgnoreCase));
}
