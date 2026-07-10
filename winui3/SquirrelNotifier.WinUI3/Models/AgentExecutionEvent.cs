// <copyright file="AgentExecutionEvent.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Models;

/// <summary>
/// エージェント実行セッションが配信するイベントの種別.
/// </summary>
internal enum AgentExecutionEventKind
{
    /// <summary>stdout の 1 行（構造化 progress event ではない通常ログ）.</summary>
    Stdout,

    /// <summary>stderr の 1 行.</summary>
    Stderr,

    /// <summary>構造化 progress event（<see cref="AgentProgressEvent"/>）.</summary>
    Progress,

    /// <summary>実行終了を示す terminal event。<see cref="AgentExecutionEvent.Outcome"/> と <see cref="AgentExecutionEvent.Result"/> を持つ.</summary>
    Completed,
}

/// <summary>
/// 実行終了時の結果分類.
/// </summary>
internal enum AgentExecutionOutcome
{
    /// <summary>ExitCode 0 で正常終了.</summary>
    Succeeded,

    /// <summary>非ゼロ ExitCode、または起動・実行エラー.</summary>
    Failed,

    /// <summary>ユーザー操作によりキャンセルされた.</summary>
    Cancelled,

    /// <summary>LauncherTimeoutMs を超過して強制終了された.</summary>
    TimedOut,
}

/// <summary>
/// エージェント実行セッション（<see cref="Services.AgentExecutionSession"/>）が UI 層へ逐次配信するイベント（#143）.
/// </summary>
internal sealed record AgentExecutionEvent
{
    /// <summary>Gets イベント種別.</summary>
    public required AgentExecutionEventKind Kind { get; init; }

    /// <summary>Gets squirrel-notifier 側で観測したイベント時刻.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Gets ログ 1 行のテキスト。Kind が Stdout / Stderr の場合のみ非 null.</summary>
    public string? Text { get; init; }

    /// <summary>Gets 構造化 progress event。Kind が Progress の場合のみ非 null.</summary>
    public AgentProgressEvent? Progress { get; init; }

    /// <summary>Gets 実行終了の結果分類。Kind が Completed の場合のみ非 null.</summary>
    public AgentExecutionOutcome? Outcome { get; init; }

    /// <summary>Gets 実行結果。Kind が Completed の場合のみ非 null.</summary>
    public LauncherResult? Result { get; init; }
}
