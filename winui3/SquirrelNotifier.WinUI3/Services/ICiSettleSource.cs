// <copyright file="ICiSettleSource.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// PR の現在の head SHA と、required checks の確定状態を 1 回だけ取得する（#456）。
/// 状態を持たない。ポーリング・待機の上限・head の移動の扱いは <c>CiSettleWaiter</c> の責務で、
/// 取得元（暫定は <c>gh api</c>、将来は thread-owl の CI 状態 tool）を差し替えても待機側は変えない.
/// </summary>
internal interface ICiSettleSource
{
    /// <summary>
    /// CI の確定状態を取得する。取得できなかった場合は例外ではなく
    /// <see cref="CiSettleState.Unavailable"/> を返す。キャンセルされた場合だけ
    /// <see cref="OperationCanceledException"/> を送出する.
    /// </summary>
    /// <param name="repository"><c>owner/repo</c> 形式のリポジトリ.</param>
    /// <param name="prNumber">PR 番号.</param>
    /// <param name="cancellationToken">取得を中断するトークン.</param>
    /// <returns>取得した時点の状態.</returns>
    Task<CiSettleSnapshot> GetAsync(string repository, int prNumber, CancellationToken cancellationToken);
}
