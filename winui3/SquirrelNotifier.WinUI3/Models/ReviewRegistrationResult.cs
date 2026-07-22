// <copyright file="ReviewRegistrationResult.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Models;

internal sealed class ReviewRegistrationResult
{
    public ReviewRegistrationOutcome Outcome { get; init; }

    public bool IsAuthenticationRequired { get; init; }

    public string ErrorMessage { get; init; } = string.Empty;
}

internal enum ReviewRegistrationOutcome
{
    /// <summary>購読中にレビューを登録できた.</summary>
    Registered,

    /// <summary>購読開始の確認または呼び出し元の操作で中断した.</summary>
    Cancelled,

    /// <summary>購読が Running に到達しなかった.</summary>
    SubscriptionStartFailed,

    /// <summary>購読開始後の queue 登録に失敗した.</summary>
    EnqueueFailed,

    /// <summary>別のレビュー登録操作が進行中であるため開始しなかった.</summary>
    AlreadyInProgress,
}
