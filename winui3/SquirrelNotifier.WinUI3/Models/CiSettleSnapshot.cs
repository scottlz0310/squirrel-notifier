// <copyright file="CiSettleSnapshot.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Models;

/// <summary>PR の CI が確定したかの区分（#456）.</summary>
internal enum CiSettleState
{
    /// <summary>required checks がすべて完了し、成功している.</summary>
    Passed,

    /// <summary>required checks のいずれかが失敗している（未完了の check が残っていても失敗を優先する）.</summary>
    Failed,

    /// <summary>required checks に未完了または未報告のものがある.</summary>
    Pending,

    /// <summary>状態を取得できなかった（<c>gh</c> が使えない・権限不足・応答を解釈できない等）.</summary>
    Unavailable,

    /// <summary>PR が merge または close されている.</summary>
    PullRequestClosed,
}

/// <summary>
/// ある時点の PR の head SHA と CI の確定状態（#456）。check の名前と状態だけを保持し、
/// コメントの本文などは持たない.
/// </summary>
/// <param name="State">CI の確定状態.</param>
/// <param name="HeadSha">状態を取得した時点の PR の head SHA。取得できなかった場合は <see langword="null"/>.</param>
/// <param name="Detail">Recent activity に残す、状態の根拠となる短い説明.</param>
internal sealed record CiSettleSnapshot(CiSettleState State, string? HeadSha, string Detail)
{
    public static CiSettleSnapshot Unavailable(string detail) => new(CiSettleState.Unavailable, null, detail);
}
