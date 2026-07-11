// <copyright file="AgentLogLine.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Models;

/// <summary>
/// ライブログウィンドウに表示する 1 行（#144）。サニタイズ・マスキング適用済みのテキストを保持する.
/// </summary>
/// <param name="Kind">行の由来（stdout / stderr / progress）。表示上の区別に使う.</param>
/// <param name="Text">表示テキスト.</param>
/// <param name="Timestamp">squirrel-notifier 側で観測したイベント時刻.</param>
internal sealed record AgentLogLine(AgentExecutionEventKind Kind, string Text, DateTimeOffset Timestamp);
