// <copyright file="AgentExecutionSession.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// 1 回のエージェント実行のライフサイクルを集約し、stdout / stderr / progress / completed の
/// 型付きイベントを実行中に逐次配信するセッション（#143）。
/// イベントは <see cref="ReadEventsAsync"/> で購読し、最終結果は <see cref="Completion"/> で待機する.
/// </summary>
internal sealed class AgentExecutionSession
{
    // 購読者が居なくてもプロセス出力の書き込みをブロックさせないため unbounded にする。
    // メモリ上限（rolling buffer）は UI 層（#144）の責務.
    private readonly Channel<AgentExecutionEvent> _channel = Channel.CreateUnbounded<AgentExecutionEvent>(
        new UnboundedChannelOptions
        {
            // stdout / stderr の pump が並行して書き込む
            SingleWriter = false,
            SingleReader = false,
        });

    private readonly TaskCompletionSource<LauncherResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TimeProvider _timeProvider;

    internal AgentExecutionSession(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <summary>Gets 実行の最終結果。terminal event（Completed）の発行と同時に完了する.</summary>
    public Task<LauncherResult> Completion => _completion.Task;

    /// <summary>
    /// 実行イベントを発生順に購読する。セッション終了（Completed 発行）後は列挙が終端する.
    /// </summary>
    /// <param name="cancellationToken">購読を中断するためのトークン。実行自体はキャンセルされない.</param>
    /// <returns>実行イベントの非同期シーケンス.</returns>
    public IAsyncEnumerable<AgentExecutionEvent> ReadEventsAsync(CancellationToken cancellationToken = default)
        => _channel.Reader.ReadAllAsync(cancellationToken);

    internal void PublishStdout(string line)
        => Publish(new AgentExecutionEvent
        {
            Kind = AgentExecutionEventKind.Stdout,
            Timestamp = _timeProvider.GetUtcNow(),
            Text = line,
        });

    internal void PublishStderr(string line)
        => Publish(new AgentExecutionEvent
        {
            Kind = AgentExecutionEventKind.Stderr,
            Timestamp = _timeProvider.GetUtcNow(),
            Text = line,
        });

    internal void PublishProgress(AgentProgressEvent progress)
        => Publish(new AgentExecutionEvent
        {
            Kind = AgentExecutionEventKind.Progress,
            Timestamp = _timeProvider.GetUtcNow(),
            Progress = progress,
        });

    // terminal event を発行してチャンネルを終端し、Completion を確定する。
    // 二重呼び出しは TryComplete / TrySetResult により無害化される.
    internal void Complete(AgentExecutionOutcome outcome, LauncherResult result)
    {
        Publish(new AgentExecutionEvent
        {
            Kind = AgentExecutionEventKind.Completed,
            Timestamp = _timeProvider.GetUtcNow(),
            Outcome = outcome,
            Result = result,
        });
        _channel.Writer.TryComplete();
        _completion.TrySetResult(result);
    }

    private void Publish(AgentExecutionEvent executionEvent)
        => _channel.Writer.TryWrite(executionEvent);
}
