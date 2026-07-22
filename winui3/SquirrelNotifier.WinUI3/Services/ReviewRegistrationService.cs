// <copyright file="ReviewRegistrationService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// 手動レビュー登録について、購読状態の確認から queue 登録までの順序と多重実行を管理する.
/// </summary>
internal sealed class ReviewRegistrationService
{
    private readonly IReviewSubscriptionService _subscriptionService;
    private readonly IEnqueueReviewService _enqueueReviewService;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public ReviewRegistrationService(
        IReviewSubscriptionService subscriptionService,
        IEnqueueReviewService enqueueReviewService)
    {
        _subscriptionService = subscriptionService;
        _enqueueReviewService = enqueueReviewService;
    }

    /// <summary>
    /// 購読が Running であることを確認した後だけレビューを登録する.
    /// </summary>
    /// <param name="reference">登録する PR.</param>
    /// <param name="reason">レビュー開始理由.</param>
    /// <param name="confirmSubscriptionStartAsync">購読停止中に開始確認を行うコールバック.</param>
    /// <param name="cancellationToken">一連の操作のキャンセル用トークン.</param>
    /// <returns>登録処理の結果.</returns>
    public async Task<ReviewRegistrationResult> RegisterAsync(
        PrReference reference,
        string reason,
        Func<CancellationToken, Task<bool>> confirmSubscriptionStartAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(confirmSubscriptionStartAsync);

        if (cancellationToken.IsCancellationRequested)
        {
            return CreateCancelledResult();
        }

        bool entered = _operationGate.Wait(0);
        if (!entered)
        {
            return new ReviewRegistrationResult { Outcome = ReviewRegistrationOutcome.AlreadyInProgress };
        }

        try
        {
            if (_subscriptionService.State != SubscriptionState.Running)
            {
                bool shouldStart = await confirmSubscriptionStartAsync(cancellationToken).ConfigureAwait(false);
                if (!shouldStart || cancellationToken.IsCancellationRequested)
                {
                    return CreateCancelledResult();
                }

                SubscriptionStartResult startResult = await _subscriptionService.StartAsync(cancellationToken).ConfigureAwait(false);
                if (!startResult.Success || _subscriptionService.State != SubscriptionState.Running)
                {
                    return new ReviewRegistrationResult
                    {
                        Outcome = ReviewRegistrationOutcome.SubscriptionStartFailed,
                        IsAuthenticationRequired = _subscriptionService.IsAuthenticationRequired,
                        ErrorMessage = startResult.ErrorMessage ?? "購読を開始できませんでした。",
                    };
                }
            }

            EnqueueReviewResult enqueueResult = await _enqueueReviewService
                .EnqueueAsync(reference, reason, cancellationToken)
                .ConfigureAwait(false);

            return enqueueResult.Success
                ? new ReviewRegistrationResult { Outcome = ReviewRegistrationOutcome.Registered }
                : new ReviewRegistrationResult
                {
                    Outcome = ReviewRegistrationOutcome.EnqueueFailed,
                    IsAuthenticationRequired = enqueueResult.IsAuthenticationRequired,
                    ErrorMessage = enqueueResult.ErrorMessage,
                };
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private static ReviewRegistrationResult CreateCancelledResult()
        => new()
        {
            Outcome = ReviewRegistrationOutcome.Cancelled,
            ErrorMessage = "レビュー登録がキャンセルされました。",
        };
}
