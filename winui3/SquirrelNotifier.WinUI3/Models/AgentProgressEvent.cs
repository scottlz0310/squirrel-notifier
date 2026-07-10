// <copyright file="AgentProgressEvent.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Models;

/// <summary>
/// エージェント（またはラッパー）が stdout へ出力する構造化 progress event（#143）。
/// phase はエージェント非依存の汎用構造（index / total / label）であり、phase 数は固定しない.
/// </summary>
/// <param name="SchemaVersion">contract のスキーマバージョン。現行は 1.</param>
/// <param name="PhaseIndex">現在の phase の 0 始まりインデックス.</param>
/// <param name="TotalPhases">ワークフロー全体の phase 数（1 以上）.</param>
/// <param name="PhaseLabel">phase の表示名（例: "修正", "Verdict 待機"）.</param>
/// <param name="Message">phase 内の補足メッセージ。省略可.</param>
/// <param name="Verdict">レビュー Verdict（例: "APPROVED"）。省略可.</param>
/// <param name="Timestamp">producer 側が付与したイベント時刻。省略可.</param>
internal sealed record AgentProgressEvent(
    int SchemaVersion,
    int PhaseIndex,
    int TotalPhases,
    string PhaseLabel,
    string? Message,
    string? Verdict,
    DateTimeOffset? Timestamp);
