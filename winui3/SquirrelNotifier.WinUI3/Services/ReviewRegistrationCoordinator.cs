// <copyright file="ReviewRegistrationCoordinator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>レビュー登録結果を画面が反映する内容へ変換する.</summary>
internal sealed record ReviewRegistrationPresentation(
    bool ClearInput,
    bool? IsAuthenticationRequired,
    string? DialogTitle,
    string? DialogMessage)
{
    public bool HasDialog => DialogTitle is not null;
}

/// <summary>
/// レビュー登録画面の入力検証と登録結果の表示分類を担当する。
/// 購読開始確認Dialogの生成・表示はUI境界として呼び出し側へ残す.
/// </summary>
internal sealed class ReviewRegistrationCoordinator
{
    private static readonly string[] _reasons =
    [
        "opened",
        "synchronized",
        "re-review-requested",
    ];

    private readonly Func<
        PrReference,
        string,
        Func<CancellationToken, Task<bool>>,
        CancellationToken,
        Task<ReviewRegistrationResult>> _registerAsync;

    public ReviewRegistrationCoordinator(ReviewRegistrationService registrationService)
    {
        ArgumentNullException.ThrowIfNull(registrationService);
        _registerAsync = registrationService.RegisterAsync;
    }

    internal ReviewRegistrationCoordinator(
        Func<
            PrReference,
            string,
            Func<CancellationToken, Task<bool>>,
            CancellationToken,
            Task<ReviewRegistrationResult>> registerAsync)
    {
        ArgumentNullException.ThrowIfNull(registerAsync);
        _registerAsync = registerAsync;
    }

    /// <summary>Gets レビュー登録理由として選択できる値.</summary>
    public static IReadOnlyList<string> Reasons => _reasons;

    /// <summary>入力を検証し、レビュー登録結果をUI向けに分類する.</summary>
    /// <param name="input">PR URLまたは短縮形式の入力.</param>
    /// <param name="reason">レビュー登録理由。未選択時は opened.</param>
    /// <param name="confirmSubscriptionStartAsync">購読停止中の開始確認コールバック.</param>
    /// <param name="cancellationToken">登録処理のキャンセル用トークン.</param>
    /// <returns>UIへ反映する登録結果.</returns>
    public async Task<ReviewRegistrationPresentation> RegisterAsync(
        string? input,
        string? reason,
        Func<CancellationToken, Task<bool>> confirmSubscriptionStartAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirmSubscriptionStartAsync);

        if (!PrReferenceParser.TryParse(input, out PrReference? reference) || reference is null)
        {
            return new ReviewRegistrationPresentation(
                ClearInput: false,
                IsAuthenticationRequired: null,
                DialogTitle: "入力エラー",
                DialogMessage: "PR URL（https://github.com/owner/repo/pull/123）または owner/repo#123 の形式で入力してください。");
        }

        string selectedReason = reason ?? "opened";
        ReviewRegistrationResult result = await _registerAsync(
            reference,
            selectedReason,
            confirmSubscriptionStartAsync,
            cancellationToken).ConfigureAwait(false);

        return DescribeResult(result, reference, selectedReason);
    }

    private static ReviewRegistrationPresentation DescribeResult(
        ReviewRegistrationResult result,
        PrReference reference,
        string reason)
        => result.Outcome switch
        {
            ReviewRegistrationOutcome.Registered => new ReviewRegistrationPresentation(
                ClearInput: true,
                IsAuthenticationRequired: null,
                DialogTitle: "レビュー登録完了",
                DialogMessage: $"{reference.Owner}/{reference.Repo}#{reference.PrNumber} を reason={reason} で登録しました。\nこの画面を閉じても登録は取り消されません。"),

            ReviewRegistrationOutcome.Cancelled or ReviewRegistrationOutcome.AlreadyInProgress =>
                new ReviewRegistrationPresentation(
                    ClearInput: false,
                    IsAuthenticationRequired: null,
                    DialogTitle: null,
                    DialogMessage: null),

            ReviewRegistrationOutcome.SubscriptionStartFailed => new ReviewRegistrationPresentation(
                ClearInput: false,
                IsAuthenticationRequired: result.IsAuthenticationRequired,
                DialogTitle: "購読開始エラー",
                DialogMessage: result.ErrorMessage),

            ReviewRegistrationOutcome.EnqueueFailed => new ReviewRegistrationPresentation(
                ClearInput: false,
                IsAuthenticationRequired: result.IsAuthenticationRequired,
                DialogTitle: "レビュー登録エラー",
                DialogMessage: result.ErrorMessage),

            _ => new ReviewRegistrationPresentation(
                ClearInput: false,
                IsAuthenticationRequired: result.IsAuthenticationRequired,
                DialogTitle: "レビュー登録エラー",
                DialogMessage: result.ErrorMessage),
        };
}
