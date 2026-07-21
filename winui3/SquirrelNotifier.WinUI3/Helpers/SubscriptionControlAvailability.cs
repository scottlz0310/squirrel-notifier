// <copyright file="SubscriptionControlAvailability.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// 購読状態から、開始・停止・リトライ操作の活性状態を決める（#202）。
/// メインウィンドウのボタンとトレイの右クリックメニューが同じ判断を共有するため、
/// UI から切り離した純関数として持つ.
/// </summary>
/// <param name="CanStart">「開始」を実行できるか.</param>
/// <param name="CanStop">「停止」を実行できるか.</param>
/// <param name="CanRetry">「リトライ」を実行できるか.</param>
internal readonly record struct SubscriptionControlAvailability(bool CanStart, bool CanStop, bool CanRetry)
{
    /// <summary>
    /// 購読状態に対応する活性状態を返す.
    /// </summary>
    /// <param name="state">現在の購読状態.</param>
    /// <returns>各操作の活性状態.</returns>
    public static SubscriptionControlAvailability For(SubscriptionState state) => state switch
    {
        SubscriptionState.Running => new(CanStart: false, CanStop: true, CanRetry: false),
        SubscriptionState.Stopped => new(CanStart: true, CanStop: false, CanRetry: false),

        // 遷移中はどちらの操作も受け付けない（二重実行を防ぐ）
        SubscriptionState.Starting or SubscriptionState.Stopping => new(CanStart: false, CanStop: false, CanRetry: false),
        SubscriptionState.Error => new(CanStart: true, CanStop: false, CanRetry: true),
        _ => new(CanStart: false, CanStop: false, CanRetry: false),
    };
}
