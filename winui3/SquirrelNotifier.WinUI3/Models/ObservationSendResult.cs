// <copyright file="ObservationSendResult.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Models;

/// <summary>review-raven への観測の送信結果.</summary>
/// <param name="Success">review-raven が受理した（<c>applied</c> または冪等な <c>duplicate</c>）か.</param>
/// <param name="Retryable">失敗したとき、後で同じ観測の最新の状態を送り直せば成功し得るか。契約の <c>retryable</c> と、通信・認証の失敗から決める.</param>
/// <param name="ErrorCode">失敗の種類（契約のエラー code、またはこの送信側が決める code）.</param>
/// <param name="Message">失敗の説明。秘密値・トークンを含めない.</param>
internal sealed record ObservationSendResult(bool Success, bool Retryable, string? ErrorCode, string? Message)
{
    /// <summary>受理された.</summary>
    internal static readonly ObservationSendResult Accepted = new(true, false, null, null);

    /// <summary>後で送り直せば成功し得る失敗を作る.</summary>
    /// <param name="errorCode">失敗の種類.</param>
    /// <param name="message">失敗の説明.</param>
    /// <returns>失敗の結果.</returns>
    internal static ObservationSendResult Transient(string errorCode, string message) => new(false, true, errorCode, message);

    /// <summary>送り直しても成功しない失敗（設定・割当・状態の矛盾）を作る.</summary>
    /// <param name="errorCode">失敗の種類.</param>
    /// <param name="message">失敗の説明.</param>
    /// <returns>失敗の結果.</returns>
    internal static ObservationSendResult Permanent(string errorCode, string message) => new(false, false, errorCode, message);
}
