// <copyright file="ReviewAutoStartPolicy.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

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

/// <summary>
/// review event 受信時に reviewer を自動起動してよいかを判定する（#254）。UI から切り離した
/// 純粋な判定のみを持ち、起動そのもの・Auto-Pause の評価・通知表示は呼び出し側の責務とする.
/// </summary>
internal static class ReviewAutoStartPolicy
{
    /// <summary>別のレビューが実行中で自動起動を見送ったときの理由.</summary>
    public const string BusyReasonText = "別のレビューが実行中のため";

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
}
