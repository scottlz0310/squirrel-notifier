// <copyright file="SubscriptionStartResult.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Models;

/// <summary>
/// 購読開始の待機結果（#208）。呼び出し側が「購読が Running になってから次の操作へ進む」
/// 判断をできるようにする.
/// <para>
/// mcp-gateway の認証要求はこの結果では表現しない。購読開始の可否は preflight（subscriber の
/// <c>--help</c>）だけで決まり、gateway への接続は Running 到達後の購読プロセスで初めて行われる。
/// 認証要求は購読ループが Error へ落ちた時点で
/// <see cref="Services.McpSubscriptionService.IsAuthenticationRequired"/> に現れるため、
/// 呼び出し側はそちらを参照する.
/// </para>
/// </summary>
internal sealed class SubscriptionStartResult
{
    public SubscriptionStartOutcome Outcome { get; init; }

    public bool Success => Outcome == SubscriptionStartOutcome.Started;

    /// <summary>Gets 失敗・タイムアウト時の原因を含むユーザー向けメッセージ.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// 購読開始の終了区分。呼び出し側は成功・失敗・タイムアウト・キャンセルを区別して扱う（#208）.
/// </summary>
internal enum SubscriptionStartOutcome
{
    /// <summary>購読が Running に到達した.</summary>
    Started,

    /// <summary>preflight 失敗・購読停止等により Running に到達しなかった.</summary>
    Failed,

    /// <summary>規定時間内に状態が確定しなかった.</summary>
    TimedOut,

    /// <summary>呼び出し側がキャンセルした.</summary>
    Cancelled,
}
