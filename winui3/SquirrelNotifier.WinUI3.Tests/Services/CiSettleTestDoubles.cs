// <copyright file="CiSettleTestDoubles.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

// CI の確定待ち（#456）のテストで共有する。待機・ゲート・起動判定のテストは、取得元と時計をこれで固定する

/// <summary>記録した順に <see cref="CiSettleSnapshot"/>（または例外）を返し、尽きたら最後の 1 件を返し続ける取得元.</summary>
internal sealed class ScriptedCiSettleSource : ICiSettleSource
{
    private readonly List<object> _script = [];
    private int _index;

    public List<(string Repository, int PrNumber, CancellationToken Token)> Calls { get; } = [];

    public static CiSettleSnapshot Snapshot(string state, string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
        => new(Enum.Parse<CiSettleState>(state), state == "Unavailable" ? null : sha, $"{state} detail");

    public void Add(params object[] items) => _script.AddRange(items);

    public Task<CiSettleSnapshot> GetAsync(string repository, int prNumber, CancellationToken cancellationToken)
    {
        Calls.Add((repository, prNumber, cancellationToken));
        object item = _script[Math.Min(_index, _script.Count - 1)];
        _index++;
        return item switch
        {
            Exception exception => throw exception,
            CiSettleSnapshot snapshot => Task.FromResult(snapshot),
            _ => throw new InvalidOperationException("スクリプトの要素が不正です。"),
        };
    }
}

/// <summary>
/// 呼び出しごとに完了を手動で制御できる取得元。CI の確認（gh）の await の間に、別の操作
/// （手動起動など）が入る状況を再現する.
/// </summary>
internal sealed class ControllableCiSettleSource : ICiSettleSource
{
    private readonly List<TaskCompletionSource<CiSettleSnapshot>> _calls = [];

    public int CallCount => _calls.Count;

    public Task<CiSettleSnapshot> GetAsync(string repository, int prNumber, CancellationToken cancellationToken)
    {
        TaskCompletionSource<CiSettleSnapshot> call = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _calls.Add(call);
        return call.Task;
    }

    public void Complete(int callIndex, CiSettleSnapshot snapshot) => _calls[callIndex].SetResult(snapshot);
}

/// <summary>
/// 時刻を手動で進める <see cref="TimeProvider"/>。作成されたタイマーを記録し、
/// <see cref="FireTimersImmediately"/> の間は、待機（<c>Task.Delay</c>）を要求された分だけ時刻を進めて即座に満了させる.
/// </summary>
internal sealed class CiSettleTestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public bool FireTimersImmediately { get; set; }

    public List<RecordingTimer> Timers { get; } = [];

    public void Advance(TimeSpan by) => _now += by;

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        RecordingTimer timer = new(callback, state, dueTime);
        Timers.Add(timer);
        if (FireTimersImmediately && dueTime > TimeSpan.Zero)
        {
            Advance(dueTime);

            // Task.Delay が ITimer を受け取る前に完了させないため、コールバックは ThreadPool へ回す
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }

        return timer;
    }

    internal sealed class RecordingTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        public TimeSpan DueTime { get; private set; } = dueTime;

        public bool IsDisposed { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            DueTime = dueTime;
            return true;
        }

        public void Dispose() => IsDisposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
