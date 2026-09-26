// <copyright file="ReviewCycleState.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Models;

/// <summary>レビューサイクルのローカル観測状態.</summary>
internal enum ReviewCycleStatus
{
    /// <summary>サイクル状態をまだ観測していない.</summary>
    Unknown,

    /// <summary>reviewer の起動を待っている.</summary>
    AwaitingReviewer,

    /// <summary>reviewer が実行中.</summary>
    ReviewerRunning,

    /// <summary>reviewer のプロセスが終了した。レビュー結果は別途確認が必要.</summary>
    ReviewerCompleted,

    /// <summary>reviewer のプロセスが失敗した.</summary>
    ReviewerFailed,
}

/// <summary>PR 単位で保持するレビューサイクルの観測状態.</summary>
internal sealed record ReviewCycleState(
    string Repository,
    int PrNumber,
    int Round,
    string LastEventId,
    string LastReason,
    ReviewCycleStatus Status,
    string? ActiveEventId,
    int? ActiveRound,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string> ProcessedEventIds,
    string? ActiveAgent = null);
