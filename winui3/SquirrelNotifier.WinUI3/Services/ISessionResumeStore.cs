// <copyright file="ISessionResumeStore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>保存済み session の検索結果.</summary>
internal enum SessionResumeLookupStatus
{
    /// <summary>有効な session が見つかった.</summary>
    Found,

    /// <summary>対応する session が保存されていない.</summary>
    NotFound,

    /// <summary>保存済み session の TTL が切れていた.</summary>
    Expired,

    /// <summary>保存時と現在の working directory が異なる.</summary>
    WorkingDirectoryMismatch,
}

/// <summary>再開可能な session の永続化情報.</summary>
/// <param name="SessionId">CLI session の UUID.</param>
/// <param name="AgentId">session を作成した launcher agent ID.</param>
/// <param name="WorkingDirectory">session を作成した絶対 working directory.</param>
/// <param name="CreatedAt">session を最初に保存した UTC 日時.</param>
/// <param name="LastUsedAt">session を最後に保存した UTC 日時.</param>
internal sealed record SessionResumeEntry(
    Guid SessionId,
    string AgentId,
    string WorkingDirectory,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt);

/// <summary>session lookup の値と miss 理由.</summary>
/// <param name="Status">lookup の状態.</param>
/// <param name="Entry"><paramref name="Status"/> が <see cref="SessionResumeLookupStatus.Found"/> の場合の entry.</param>
internal sealed record SessionResumeLookupResult(
    SessionResumeLookupStatus Status,
    SessionResumeEntry? Entry);

/// <summary>PR・role・agent ごとの resume session を永続化する.</summary>
internal interface ISessionResumeStore
{
    Task<SessionResumeLookupResult> TryGetAsync(
        ReviewEvent reviewEvent,
        LauncherRole role,
        string agentId,
        string workingDirectory,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        ReviewEvent reviewEvent,
        LauncherRole role,
        string agentId,
        string workingDirectory,
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(
        ReviewEvent reviewEvent,
        LauncherRole role,
        string agentId,
        CancellationToken cancellationToken = default);
}
