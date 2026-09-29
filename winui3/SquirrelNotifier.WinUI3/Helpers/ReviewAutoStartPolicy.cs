// <copyright file="ReviewAutoStartPolicy.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>レビュー起動が人の操作によるものか、queue event による自動起動かの区別（#254）.</summary>
internal enum ReviewStartTrigger
{
    /// <summary>ボタン操作など、人が起点の起動.</summary>
    Manual,

    /// <summary>review event 受信による自動起動.</summary>
    Automatic,
}

/// <summary>自動レビュー開始の判定結果（#254）.</summary>
internal enum ReviewAutoStartOutcome
{
    /// <summary>自動起動する.</summary>
    Start,

    /// <summary>設定が off のため自動起動しない.</summary>
    SkippedDisabled,

    /// <summary>reviewer side のアクションを伴わない reason のため自動起動しない.</summary>
    SkippedUnsupportedReason,

    /// <summary>別のレビューが実行中のため自動起動しない.</summary>
    SkippedBusy,
}

/// <summary>CI の確定待ちの判定の結果（暫定、#456）.</summary>
internal enum CiSettleGateAction
{
    /// <summary>確定した、または待たない理由があるため、起動する.</summary>
    Start,

    /// <summary>CI が未確定のため保留し、再評価まで待つ.</summary>
    Hold,

    /// <summary>PR が merge または close されたため、起動しない.</summary>
    SkipPullRequestClosed,
}

/// <summary>CI の確定待ちの判定と、Recent activity・通知へ残す文言（暫定、#456）.</summary>
/// <param name="Action">判定の結果.</param>
/// <param name="ActivityLog">Recent activity に残す説明。残す必要が無い場合は <see langword="null"/>.</param>
/// <param name="HoldReasonText">保留する場合の、保留の記録に残す理由.</param>
/// <param name="StartNote">起動する場合に通知へ添える注記。無い場合は <see langword="null"/>.</param>
internal sealed record CiSettleGateDecision(
    CiSettleGateAction Action,
    string? ActivityLog = null,
    string? HoldReasonText = null,
    string? StartNote = null);

/// <summary>
/// review event 受信時に reviewer を自動起動してよいかを判定する（#254）。UI から切り離した
/// 純粋な判定のみを持ち、起動そのもの・Auto-Pause の評価・通知表示は呼び出し側の責務とする.
/// </summary>
internal static class ReviewAutoStartPolicy
{
    /// <summary>別のレビューが実行中で保留したときの、通知に出す短い理由（#340）.</summary>
    public const string BusyHoldLabel = "別のレビューが実行中";

    /// <summary>Auto-Pause 中で保留したときの、通知に出す短い理由（#340）.</summary>
    public const string AutoPausedHoldLabel = "Auto-Pause 中";

    /// <summary>
    /// required checks の確定を待って保留したときの、通知に出す短い理由（暫定、#456）.
    /// thread-owl が CI の状態を返す tool を提供し、待機の位置づけが変わったら、この節ごと撤去する.
    /// </summary>
    public const string CiSettleHoldLabel = "CI 完了待ち";

    /// <summary>CI 待機の上限に達して起動したときの、Recent activity と通知に残す文言（暫定、#456）.</summary>
    public const string CiSettleTimedOutText = "CI 待機の上限に達したため起動";

    /// <summary>別のレビューが実行中で自動起動を見送ったときの理由.</summary>
    public const string BusyReasonText = $"{BusyHoldLabel}のため";

    private const string _unsupportedReasonText = "reviewer 側のアクションを伴わない reason のため";

    /// <summary>
    /// 受信した review event に対する自動起動の可否を判定する.
    /// </summary>
    /// <param name="autoStartEnabled">「レビュー自動開始」設定が on か.</param>
    /// <param name="reason">review event の reason.</param>
    /// <param name="isReviewBusy">別のレビュー起動が進行中または実行中か.</param>
    /// <returns>判定結果.</returns>
    public static ReviewAutoStartOutcome Evaluate(bool autoStartEnabled, string reason, bool isReviewBusy)
    {
        if (!autoStartEnabled)
        {
            return ReviewAutoStartOutcome.SkippedDisabled;
        }

        if (!ReviewNotificationPolicy.ShouldOfferReviewerAction(reason))
        {
            return ReviewAutoStartOutcome.SkippedUnsupportedReason;
        }

        // 落としたイベントは Recent review events に残るため、実行終了後の自動リトライは行わず
        // 手動起動で拾えるようにする（#254 の初期スコープ）
        if (isReviewBusy)
        {
            return ReviewAutoStartOutcome.SkippedBusy;
        }

        return ReviewAutoStartOutcome.Start;
    }

    /// <summary>
    /// Auto-Pause（#147）が Paused と判定したときに override 確認ダイアログを出してよいかを返す。
    /// 自動起動は無人で走るため、応答されないダイアログを出して待つことはしない（#254）.
    /// </summary>
    /// <param name="trigger">起動の起点.</param>
    /// <returns>確認ダイアログを出してよい場合は <see langword="true"/>.</returns>
    public static bool AllowsAutoPauseOverridePrompt(ReviewStartTrigger trigger)
        => trigger == ReviewStartTrigger.Manual;

    /// <summary>
    /// 自動起動を見送った理由の表示文言を返す。<see cref="ReviewAutoStartOutcome.Start"/> と
    /// <see cref="ReviewAutoStartOutcome.SkippedDisabled"/> は記録の対象外のため <see langword="null"/> を返す
    /// （設定 off のときに毎イベント行を残すと、off の挙動が現行と変わってしまう）。
    /// <see cref="ReviewAutoStartOutcome.SkippedBusy"/> は見送りではなく保留として記録するため対象外（#339）.
    /// </summary>
    /// <param name="outcome">判定結果.</param>
    /// <returns>記録する理由。記録しない場合は <see langword="null"/>.</returns>
    public static string? DescribeSkipReason(ReviewAutoStartOutcome outcome)
        => outcome switch
        {
            ReviewAutoStartOutcome.SkippedUnsupportedReason => _unsupportedReasonText,
            _ => null,
        };

    /// <summary>
    /// CI の待機結果（<c>CiSettleWaiter</c>）を、自動起動の可否へ写す（暫定、#456）。
    /// この待機は最適化であり安全性の gate ではないため、確定・失敗・上限・取得不能のいずれでも起動する。
    /// 起動しないのは、PR が閉じている場合だけである.
    /// </summary>
    /// <param name="result">待機の判定結果.</param>
    /// <returns>自動起動の可否と、残す文言.</returns>
    public static CiSettleGateDecision DecideCiSettle(CiSettleWaitResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Outcome switch
        {
            CiSettleWaitOutcome.Waiting => new CiSettleGateDecision(
                CiSettleGateAction.Hold,
                ActivityLog: result.HeadMoved ? $"head が更新されたため、新しい head の CI を待ち直します（{result.Detail}）。" : null,
                HoldReasonText: $"{CiSettleHoldLabel}（{result.Detail}）。確定後に自動起動します"),

            // 待たずに確定した場合は、通常の自動起動と記録が変わらないよう何も残さない
            CiSettleWaitOutcome.Settled => new CiSettleGateDecision(
                CiSettleGateAction.Start,
                ActivityLog: result.Waited > TimeSpan.Zero ? $"CI が確定しました（{result.Detail}。待機 {FormatDuration(result.Waited)}）。" : null),

            // 失敗はレビューの入力になるため、未完了の check が残っていても待たない
            CiSettleWaitOutcome.Failed => new CiSettleGateDecision(
                CiSettleGateAction.Start,
                ActivityLog: $"CI に失敗があるため、待たずに起動します（{result.Detail}）。"),

            // reviewer は従来どおり CI: pending と判定する
            CiSettleWaitOutcome.TimedOut => new CiSettleGateDecision(
                CiSettleGateAction.Start,
                ActivityLog: $"{CiSettleTimedOutText}します（{result.Detail}。待機 {FormatDuration(result.Waited)}）。",
                StartNote: CiSettleTimedOutText),

            CiSettleWaitOutcome.Unavailable => new CiSettleGateDecision(
                CiSettleGateAction.Start,
                ActivityLog: $"CI の状態を取得できないため、待たずに起動します: {result.Detail}"),

            CiSettleWaitOutcome.PullRequestClosed => new CiSettleGateDecision(
                CiSettleGateAction.SkipPullRequestClosed,
                ActivityLog: "PR が merge または close されているため、自動起動しません。"),

            _ => throw new ArgumentOutOfRangeException(nameof(result), result.Outcome, "未対応の待機結果です。"),
        };
    }

    private static string FormatDuration(TimeSpan duration)
        => duration.TotalMinutes >= 1
            ? $"{(int)duration.TotalMinutes} 分 {duration.Seconds} 秒"
            : $"{(int)duration.TotalSeconds} 秒";
}
