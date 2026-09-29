// <copyright file="CiSettleWaiter.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Globalization;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// 待機の間隔と上限（#456）。用途ごとに呼び出し側が決める。起動前のゲートと、
/// 完了通知の直前に 1 回だけ待つ用途では、上限が別の値になり得る.
/// </summary>
/// <param name="Interval">未確定のとき、次に確認するまでの間隔.</param>
/// <param name="MaxWait">未確定のまま待つ上限。待機の開始（最初に未確定を観測した時点）からの経過時間.</param>
internal readonly record struct CiSettleWaitOptions(TimeSpan Interval, TimeSpan MaxWait);

/// <summary><see cref="CiSettleWaiter"/> の判定の区分（#456）.</summary>
internal enum CiSettleWaitOutcome
{
    /// <summary>未確定。<see cref="CiSettleWaitResult.RetryAfter"/> 後に確認し直す.</summary>
    Waiting,

    /// <summary>required checks がすべて完了し、成功している.</summary>
    Settled,

    /// <summary>失敗が出た。未完了の check が残っていても、失敗を優先して待たない.</summary>
    Failed,

    /// <summary>上限に達した。CI は未確定のまま.</summary>
    TimedOut,

    /// <summary>状態を取得できなかった。待機を続けても取得できる保証が無いため、待たない.</summary>
    Unavailable,

    /// <summary>PR が merge または close された.</summary>
    PullRequestClosed,
}

/// <summary><see cref="CiSettleWaiter"/> の判定結果（#456）.</summary>
/// <param name="Outcome">判定の区分.</param>
/// <param name="HeadSha">判定に使った時点の PR の head SHA。取得できなかった場合は <see langword="null"/>.</param>
/// <param name="Detail">Recent activity に残す、判定の根拠となる短い説明.</param>
/// <param name="Waited">未確定を最初に観測してからの経過時間。待たずに確定した場合は <see cref="TimeSpan.Zero"/>.</param>
/// <param name="RetryAfter">
/// <see cref="CiSettleWaitOutcome.Waiting"/> のとき、次に確認するまでの間隔。
/// <see cref="CiSettleWaitOptions.Interval"/> と、残りの上限（<see cref="CiSettleWaitOptions.MaxWait"/> - <paramref name="Waited"/>）の小さいほうで、
/// 上限を越えて待たない.
/// </param>
/// <param name="HeadMoved">今回の確認で head が動いたことを検出し、新しい head に対して待ち直したか.</param>
internal sealed record CiSettleWaitResult(
    CiSettleWaitOutcome Outcome,
    string? HeadSha,
    string Detail,
    TimeSpan Waited,
    TimeSpan RetryAfter = default,
    bool HeadMoved = false)
{
    /// <summary>Gets a value indicating whether 待機が終わった（<see cref="CiSettleWaitOutcome.Waiting"/> 以外）か.</summary>
    public bool IsTerminal => Outcome != CiSettleWaitOutcome.Waiting;
}

/// <summary>
/// CI の確定を待つ（#456）。取得は <see cref="ICiSettleSource"/> に任せ、ここでは待機の規則だけを持つ:
/// 待機の開始時刻、上限、確認の間隔、head の移動、キャンセル.
/// </summary>
/// <remarks>
/// <para>
/// 2 通りの使い方ができる。<see cref="CheckAsync"/> は 1 回だけ確認して待機の状態を進めるため、
/// 再評価の契機を呼び出し側が持つ場合（保留キュー）に使う。<see cref="WaitAsync"/> は終端まで待つ
/// （完了通知の直前に 1 回だけ待つ用途）。どちらも待機の状態を PR ごとに共有する.
/// </para>
/// <para>
/// 待機の状態は PR ごとに持つ。head が動いた場合は新しい head に対して待ち直す（開始時刻と上限を数え直す）。
/// 前回の確認から上限以上あいた場合も、連続した待機ではないため待ち直す.
/// </para>
/// </remarks>
internal sealed class CiSettleWaiter
{
    private readonly ICiSettleSource _source;
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();
    private readonly Dictionary<string, WaitState> _waits = new(StringComparer.Ordinal);

    public CiSettleWaiter(ICiSettleSource source, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// CI の状態を 1 回確認し、待機の状態を進めて判定を返す。
    /// 取得元が例外を送出した場合も <see cref="CiSettleWaitOutcome.Unavailable"/> にする
    /// （この待機は最適化であり、待てないことで先へ進めなくしないため）。
    /// キャンセルされた場合だけ <see cref="OperationCanceledException"/> を送出する.
    /// </summary>
    /// <param name="repository"><c>owner/repo</c> 形式のリポジトリ.</param>
    /// <param name="prNumber">PR 番号.</param>
    /// <param name="options">間隔と上限.</param>
    /// <param name="cancellationToken">取得を中断するトークン.</param>
    /// <returns>判定結果.</returns>
    public async Task<CiSettleWaitResult> CheckAsync(
        string repository,
        int prNumber,
        CiSettleWaitOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ValidateOptions(options);

        CiSettleSnapshot snapshot = await FetchAsync(repository, prNumber, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        string key = GetKey(repository, prNumber);

        if (snapshot.State != CiSettleState.Pending)
        {
            return new CiSettleWaitResult(
                ToOutcome(snapshot.State),
                snapshot.HeadSha,
                snapshot.Detail,
                EndWait(key, now, GetInterruptionThreshold(options)));
        }

        lock (_lock)
        {
            bool headMoved = false;
            if (_waits.TryGetValue(key, out WaitState? wait))
            {
                if (!string.Equals(wait.HeadSha, snapshot.HeadSha, StringComparison.Ordinal))
                {
                    headMoved = true;
                    wait = null;
                }
                else if (now - wait.LastCheckedAt >= GetInterruptionThreshold(options))
                {
                    wait = null;
                }
            }

            wait ??= new WaitState(snapshot.HeadSha, now);
            wait.LastCheckedAt = now;
            _waits[key] = wait;

            TimeSpan waited = now - wait.StartedAt;
            if (waited >= options.MaxWait)
            {
                _waits.Remove(key);
                return new CiSettleWaitResult(CiSettleWaitOutcome.TimedOut, snapshot.HeadSha, snapshot.Detail, waited);
            }

            // 間隔が上限を割り切らない場合に、次の確認が上限を越えないよう、残りの上限で頭打ちにする
            // （例: 間隔 30 秒・上限 45 秒なら、30 秒後の次は 15 秒後に確認して、45 秒で打ち切る）
            TimeSpan retryAfter = TimeSpan.FromTicks(Math.Min(options.Interval.Ticks, (options.MaxWait - waited).Ticks));
            return new CiSettleWaitResult(
                CiSettleWaitOutcome.Waiting,
                snapshot.HeadSha,
                snapshot.Detail,
                waited,
                retryAfter,
                headMoved);
        }
    }

    /// <summary>
    /// 終端（<see cref="CiSettleWaitResult.IsTerminal"/>）まで、<see cref="CiSettleWaitOptions.Interval"/> ごとに確認して待つ。
    /// キャンセルされた場合は待機の状態を破棄し、<see cref="OperationCanceledException"/> を送出する.
    /// </summary>
    /// <param name="repository"><c>owner/repo</c> 形式のリポジトリ.</param>
    /// <param name="prNumber">PR 番号.</param>
    /// <param name="options">間隔と上限.</param>
    /// <param name="cancellationToken">待機を中断するトークン.</param>
    /// <returns>終端の判定結果.</returns>
    public async Task<CiSettleWaitResult> WaitAsync(
        string repository,
        int prNumber,
        CiSettleWaitOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                CiSettleWaitResult result = await CheckAsync(repository, prNumber, options, cancellationToken).ConfigureAwait(false);
                if (result.IsTerminal)
                {
                    return result;
                }

                await Task.Delay(result.RetryAfter, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            Forget(repository, prNumber);
            throw;
        }
    }

    /// <summary>
    /// PR が待機中か（未確定を観測し、まだ終端に達していないか）を返す。
    /// 確認の間隔ごとに繰り返される再評価を、呼び出し側が記録の対象から外すために使う.
    /// </summary>
    /// <param name="repository"><c>owner/repo</c> 形式のリポジトリ.</param>
    /// <param name="prNumber">PR 番号.</param>
    /// <returns>待機中の場合は <see langword="true"/>.</returns>
    public bool IsWaiting(string repository, int prNumber)
    {
        lock (_lock)
        {
            return _waits.ContainsKey(GetKey(repository, prNumber));
        }
    }

    /// <summary>
    /// PR の待機の状態を破棄する。レビューを別の経路で始めた場合など、待機が不要になったときに呼ぶ.
    /// </summary>
    /// <param name="repository"><c>owner/repo</c> 形式のリポジトリ.</param>
    /// <param name="prNumber">PR 番号.</param>
    public void Forget(string repository, int prNumber)
    {
        lock (_lock)
        {
            _waits.Remove(GetKey(repository, prNumber));
        }
    }

    // GitHub のリポジトリ名は大文字小文字を区別しないため、表記揺れのイベントも同じ PR として扱う
    private static string GetKey(string repository, int prNumber)
        => $"{repository.ToUpperInvariant()}#{prNumber.ToString(CultureInfo.InvariantCulture)}";

    private static void ValidateOptions(CiSettleWaitOptions options)
    {
        if (options.Interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "CI 確認の間隔は正の値である必要があります。");
        }

        if (options.MaxWait <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "CI 待機の上限は正の値である必要があります。");
        }
    }

    private static CiSettleWaitOutcome ToOutcome(CiSettleState state)
        => state switch
        {
            CiSettleState.Passed => CiSettleWaitOutcome.Settled,
            CiSettleState.Failed => CiSettleWaitOutcome.Failed,
            CiSettleState.PullRequestClosed => CiSettleWaitOutcome.PullRequestClosed,
            CiSettleState.Unavailable => CiSettleWaitOutcome.Unavailable,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "未確定は待機の状態を進める経路で扱います。"),
        };

    private async Task<CiSettleSnapshot> FetchAsync(string repository, int prNumber, CancellationToken cancellationToken)
    {
        try
        {
            return await _source.GetAsync(repository, prNumber, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CiSettleSnapshot.Unavailable($"CI の状態の取得中に {ex.GetType().Name} が発生しました: {ex.Message}");
        }
    }

    // 連続した待機の確認は、間隔（と多少の遅れ）ごとに続く。それより大きく途切れていたら連続した待機ではない
    // （別のレビューが長く実行されていた、など）。上限だけを基準にすると、上限が間隔以下のとき、待機自身の確認の間隔が
    // 途切れと判定され、待ち直しを繰り返して上限に達しなくなるため、間隔の 2 倍を下限にする
    private static TimeSpan GetInterruptionThreshold(CiSettleWaitOptions options)
        => options.MaxWait > options.Interval * 2 ? options.MaxWait : options.Interval * 2;

    // 待機が終わったら状態を破棄し、待った時間を返す。待たずに確定した場合、
    // および確認が途切れて連続した待機ではなくなっていた場合は 0
    private TimeSpan EndWait(string key, DateTimeOffset now, TimeSpan interruptionThreshold)
    {
        lock (_lock)
        {
            if (!_waits.Remove(key, out WaitState? wait) || now - wait.LastCheckedAt >= interruptionThreshold)
            {
                return TimeSpan.Zero;
            }

            return now - wait.StartedAt;
        }
    }

    private sealed class WaitState(string? headSha, DateTimeOffset startedAt)
    {
        public string? HeadSha { get; } = headSha;

        public DateTimeOffset StartedAt { get; } = startedAt;

        public DateTimeOffset LastCheckedAt { get; set; } = startedAt;
    }
}
