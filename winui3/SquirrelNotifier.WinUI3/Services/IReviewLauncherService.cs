// <copyright file="IReviewLauncherService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Threading;
using System.Threading.Tasks;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

internal interface IReviewLauncherService
{
    /// <summary>
    /// 実行が終了し、<see cref="IsRunning"/> が <see langword="false"/> に戻った後に発生する（#339）。
    /// セッションの Completion は <see cref="IsRunning"/> を戻す前に確定するため、次の起動の判定にはこちらを使う。
    /// 呼び出し元のスレッドは保証しない.
    /// </summary>
    event EventHandler? RunCompleted;

    bool IsRunning { get; }

    Task<LauncherResult> LaunchAsync(ReviewEvent reviewEvent, LauncherRole role, CancellationToken cancellationToken);

    /// <summary>
    /// 実行を開始し、stdout / stderr / progress / completed を逐次購読できるセッションを返す（#143）。
    /// 既に実行中の場合は、即座に Failed の terminal event を持つセッションを返す（同時実行抑止）.
    /// </summary>
    /// <param name="reviewEvent">起動対象のレビューイベント.</param>
    /// <param name="role">使用するランチャースロット.</param>
    /// <param name="cancellationToken">実行をキャンセルするためのトークン.</param>
    /// <returns>実行イベントの購読と最終結果の待機に使うセッション.</returns>
    AgentExecutionSession StartSession(ReviewEvent reviewEvent, LauncherRole role, CancellationToken cancellationToken);

    void Cancel();

    Task<string> BuildCommandLineAsync(
        ReviewEvent reviewEvent,
        LauncherRole role,
        CancellationToken cancellationToken = default);
}
