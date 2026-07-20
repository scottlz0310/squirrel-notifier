// <copyright file="McpLoginResult.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Models;

/// <summary>
/// mcp-gateway への device flow login の最終結果（#183）.
/// </summary>
internal sealed class McpLoginResult
{
    public McpLoginOutcome Outcome { get; init; }

    public bool Success => Outcome == McpLoginOutcome.Succeeded;

    /// <summary>Gets 失敗・タイムアウト時の原因を含むユーザー向けメッセージ.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Gets subscriber が返した構造化エラーコード（取得できた場合のみ）.</summary>
    public string? ErrorCode { get; init; }
}

/// <summary>
/// login の終了区分。UI は成功・失敗・キャンセル・タイムアウトを区別して表示する（#183 AC）.
/// </summary>
internal enum McpLoginOutcome
{
    /// <summary>認証成功。トークンがキャッシュされた.</summary>
    Succeeded,

    /// <summary>認証失敗（subscriber 未検出・古いバージョン・gateway 到達不可・device flow 拒否等）.</summary>
    Failed,

    /// <summary>ユーザーがキャンセルした.</summary>
    Cancelled,

    /// <summary>承認待ちが規定時間内に完了しなかった.</summary>
    TimedOut,
}
