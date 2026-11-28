// <copyright file="UpdateApplyCoordinator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>更新の適用を依頼する側から見た操作.</summary>
internal interface IUpdateApplier
{
    /// <summary>Gets a value indicating whether 適用を依頼されていて、まだ適用の開始前か.</summary>
    bool IsWaiting { get; }

    /// <summary>Gets a value indicating whether 適用待ちまたは適用中か。この間は新しいレビューを開始しない.</summary>
    bool IsPending { get; }

    Task RequestAsync();

    void Cancel();
}

/// <summary>適用の直前と、適用できなかった場合の処理.</summary>
internal interface IUpdateRestartPreparation
{
    /// <summary>終了させられても失われないように状態を保存し、受信を止める.</summary>
    /// <returns>再起動するプロセスに渡す引数.</returns>
    Task<string> PrepareAsync();

    /// <summary><see cref="PrepareAsync"/> の前の状態に戻す.</summary>
    /// <returns>復元の完了を表す Task.</returns>
    Task RecoverAsync();
}

/// <summary>
/// 利用者が依頼した更新を、レビューの終了を待って適用する（#569）.
/// </summary>
/// <remarks>スレッドセーフではない。UI スレッドからの利用を前提とする.</remarks>
/// <param name="accessor">更新の適用を行う境界.</param>
/// <param name="isReviewActive">レビューの起動処理中または実行中か.</param>
/// <param name="getProcessUptime">このプロセスの起動からの経過時間.</param>
/// <param name="preparation">適用の前後の処理.</param>
/// <param name="loggingService">進行と失敗を記録するログ.</param>
/// <param name="reportFailure">適用できなかった理由を利用者へ知らせる.</param>
/// <param name="requestReevaluation">UI スレッドで保留の再評価（と適用の再試行）を依頼する.</param>
/// <param name="timeProvider">起動からの経過を待つタイマーの提供元.</param>
internal sealed class UpdateApplyCoordinator(
    IPackageUpdateAccessor accessor,
    Func<bool> isReviewActive,
    Func<TimeSpan> getProcessUptime,
    IUpdateRestartPreparation preparation,
    LoggingService loggingService,
    Action<string> reportFailure,
    Action requestReevaluation,
    TimeProvider timeProvider) : IUpdateApplier, IDisposable
{
    // Windows は起動から 60 秒未満のプロセスを再起動しないため、余裕を足して待つ
    internal static readonly TimeSpan MinimumUptime = TimeSpan.FromSeconds(65);

    private ApplyState _state = ApplyState.Idle;
    private ITimer? _uptimeTimer;

    private enum ApplyState
    {
        Idle,
        Waiting,
        Applying,
    }

    public bool IsWaiting => _state == ApplyState.Waiting;

    public bool IsPending => _state != ApplyState.Idle;

    public async Task RequestAsync()
    {
        if (_state != ApplyState.Idle)
        {
            return;
        }

        _state = ApplyState.Waiting;
        await loggingService.WriteAsync("[Update] 更新の適用を受け付けました。実行中のレビューがなくなったら適用します。");
        await TryApplyAsync();
    }

    public void Cancel()
    {
        if (_state != ApplyState.Waiting)
        {
            return;
        }

        StopUptimeTimer();
        _state = ApplyState.Idle;
        _ = loggingService.WriteAsync("[Update] 更新の適用を取りやめました。");
        requestReevaluation();
    }

    /// <summary>適用待ちの場合に、条件がそろっていれば適用する。レビューの状態が変わるたびに呼ぶ.</summary>
    /// <returns>適用を見送るか、適用がプロセスを終了させずに終わるまでの Task.</returns>
    public async Task TryApplyAsync()
    {
        if (_state != ApplyState.Waiting || isReviewActive())
        {
            return;
        }

        TimeSpan remaining = MinimumUptime - getProcessUptime();
        if (remaining > TimeSpan.Zero)
        {
            StopUptimeTimer();
            _uptimeTimer = timeProvider.CreateTimer(_ => requestReevaluation(), null, remaining, Timeout.InfiniteTimeSpan);
            return;
        }

        StopUptimeTimer();
        _state = ApplyState.Applying;
        string failure;
        try
        {
            Uri uri = accessor.AppInstallerUri
                ?? throw new InvalidOperationException(".appinstaller の URI を取得できません。");
            string restartArguments = await preparation.PrepareAsync();
            await loggingService.WriteAsync($"[Update] 更新を適用して再起動します: {uri.AbsoluteUri}");
            await accessor.ApplyAsync(uri, restartArguments);

            // 適用できる場合はここへ到達する前にプロセスが終了する
            failure = "アプリが再起動されませんでした。更新が適用されている場合は、次の起動から新しい版になります。";
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }

        await loggingService.WriteAsync($"[Update] 更新の適用に失敗しました: {failure}");
        try
        {
            await preparation.RecoverAsync();
        }
        catch (Exception ex)
        {
            await loggingService.WriteAsync($"[Update] 適用前の状態に戻せませんでした: {ex.Message}");
            failure = $"{failure} 適用前の状態へ戻す処理にも失敗しました: {ex.Message}";
        }

        _state = ApplyState.Idle;
        reportFailure(failure);
        requestReevaluation();
    }

    public void Dispose() => StopUptimeTimer();

    private void StopUptimeTimer()
    {
        _uptimeTimer?.Dispose();
        _uptimeTimer = null;
    }
}
