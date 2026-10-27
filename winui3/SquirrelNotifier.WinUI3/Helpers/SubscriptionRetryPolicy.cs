// <copyright file="SubscriptionRetryPolicy.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>購読ループの失敗に対する次の一手.</summary>
/// <param name="ShouldRetry">リトライする場合は true。false なら確定エラーとして扱う.</param>
/// <param name="DelayMs">次の試行までの待機時間.</param>
/// <param name="IsWaitingForDependency">依存サービスの起動待ち経路での判断かどうか（表示文言の切り替えに使う）.</param>
internal readonly record struct SubscriptionRetryDecision(bool ShouldRetry, int DelayMs, bool IsWaitingForDependency);

/// <summary>
/// 購読ループの失敗をクラス分けし、リトライ間隔と打ち切りを決める（#236）。
/// PC 起動直後は Docker / WSL2 / mcp-gateway コンテナの初期化が終わる前に接続を試みるため、
/// 「接続拒否」は設定不備ではなく依存サービスが未 Ready なだけであることが多い。指数バックオフの
/// 予算（既定で約 31 秒）ではコールドスタートに足りず、購読が止まったまま復帰しなかった。
/// 接続拒否だけは固定間隔で長めに待ち、待っても解決しないエラー（認証・404 等）は従来どおり
/// 指数バックオフで早期に確定させる.
/// </summary>
internal static class SubscriptionRetryPolicy
{
    /// <summary>依存サービス未 Ready とみなすエラータグ（<c>fetch failed</c> / <c>ECONNREFUSED</c>）.</summary>
    public const string DependencyNotReadyErrorTag = "[CONN_REFUSED]";

    /// <summary>ユーザーの明示的な再認証操作が必要なエラータグ.</summary>
    public const string AuthenticationRequiredErrorTag = "[AUTH_REQUIRED]";

    /// <summary>依存サービスの起動待ちリトライ間隔.</summary>
    public const int DependencyWaitIntervalMs = 5000;

    /// <summary>依存サービスの起動待ちに費やす上限。超過したら確定エラーにする.</summary>
    public const int DefaultDependencyWaitBudgetMs = 300000;

    /// <summary>確定エラー後の自動回復試行の初回待機間隔（60 秒）.</summary>
    public const int InitialRecoveryIntervalMs = 60000;

    /// <summary>確定エラー後の自動回復試行の最大待機間隔（5 分）.</summary>
    public const int MaxRecoveryIntervalMs = 300000;

    private const int _initialBackoffMs = 1000;
    private const int _maxBackoffMs = 32000;

    /// <summary>
    /// 失敗 1 回分に対する次の一手を決める.
    /// </summary>
    /// <param name="errorTag"><c>GetErrorInfo</c> が返したエラータグ.</param>
    /// <param name="consecutiveFailureCount">直近の成功以降に連続した失敗回数（今回の失敗を含む、1 以上）.</param>
    /// <param name="consecutiveFailureElapsedMs">連続失敗が始まってからの経過時間.</param>
    /// <param name="maxRetries">依存サービス待ち以外のエラーに許すリトライ回数.</param>
    /// <param name="dependencyWaitBudgetMs">依存サービスの起動待ちに費やす上限.</param>
    /// <returns>次の一手.</returns>
    public static SubscriptionRetryDecision Decide(
        string errorTag,
        int consecutiveFailureCount,
        long consecutiveFailureElapsedMs,
        int maxRetries,
        int dependencyWaitBudgetMs = DefaultDependencyWaitBudgetMs)
    {
        if (string.Equals(errorTag, DependencyNotReadyErrorTag, StringComparison.Ordinal))
        {
            return consecutiveFailureElapsedMs >= dependencyWaitBudgetMs
                ? new SubscriptionRetryDecision(false, 0, true)
                : new SubscriptionRetryDecision(true, DependencyWaitIntervalMs, true);
        }

        return consecutiveFailureCount > maxRetries
            ? new SubscriptionRetryDecision(false, 0, false)
            : new SubscriptionRetryDecision(true, CalculateBackoffMs(consecutiveFailureCount), false);
    }

    /// <summary>
    /// 確定エラー後にバックグラウンドでの自動回復試行を継続すべきかどうかを判定する.
    /// 認証エラー（ユーザーの明示的なログイン操作が必須）は自動回復しない.
    /// </summary>
    /// <param name="errorTag"><c>GetErrorInfo</c> が返したエラータグ.</param>
    /// <returns>自動回復を試みる場合は true.</returns>
    public static bool ShouldAutoRecover(string errorTag)
        => !string.Equals(errorTag, AuthenticationRequiredErrorTag, StringComparison.Ordinal);

    /// <summary>
    /// 確定エラー後の自動回復試行に対する待機時間を計算する.
    /// 初回 60 秒から指数バックオフ（60s → 120s → 240s）で増加し、最大 5 分（300s）で頭打ちにする.
    /// </summary>
    /// <param name="recoveryAttempt">自動回復の試行回数（1 以上）.</param>
    /// <returns>次の回復試行までの待機時間（ミリ秒）.</returns>
    public static int CalculateRecoveryBackoffMs(int recoveryAttempt)
    {
        if (recoveryAttempt <= 1)
        {
            return InitialRecoveryIntervalMs;
        }

        double delay = InitialRecoveryIntervalMs * Math.Pow(2, recoveryAttempt - 1);
        return delay >= MaxRecoveryIntervalMs ? MaxRecoveryIntervalMs : (int)delay;
    }

    private static int CalculateBackoffMs(int consecutiveFailureCount)
    {
        if (consecutiveFailureCount <= 1)
        {
            return _initialBackoffMs;
        }

        double delay = _initialBackoffMs * Math.Pow(2, consecutiveFailureCount - 1);
        return delay >= _maxBackoffMs ? _maxBackoffMs : (int)delay;
    }
}
