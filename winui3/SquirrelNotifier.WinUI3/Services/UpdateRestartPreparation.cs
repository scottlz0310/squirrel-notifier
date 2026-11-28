// <copyright file="UpdateRestartPreparation.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// 更新の適用の前に、受信を止めて保留中のレビューを保存する（#569）.
/// </summary>
/// <param name="host">アプリの他の部分への操作.</param>
internal sealed class UpdateRestartPreparation(AppUpdateHost host) : IUpdateRestartPreparation
{
    private bool _wasSubscribing;

    public async Task<string> PrepareAsync()
    {
        _wasSubscribing = host.GetSubscriptionState() is SubscriptionState.Starting or SubscriptionState.Running or SubscriptionState.Error;
        bool wasWindowVisible = host.IsWindowVisible();

        // 受信を先に止め、適用が終わるまで開始させない。購読の子プロセスを残さず、保存の後に保留が増えないようにする
        await host.SuspendSubscriptionAsync();
        host.CarryOver.Save(host.PendingReviews.Snapshot());
        return wasWindowVisible ? string.Empty : "--tray";
    }

    public Task RecoverAsync()
    {
        // 受信の復旧を先に行う。保存した分の削除に失敗しても、購読を止めたままにしない
        host.ResumeSubscription();
        if (_wasSubscribing)
        {
            host.StartSubscription();
        }

        // 保留はメモリに残っているため、保存した分は破棄する
        host.CarryOver.Delete();
        return Task.CompletedTask;
    }
}
