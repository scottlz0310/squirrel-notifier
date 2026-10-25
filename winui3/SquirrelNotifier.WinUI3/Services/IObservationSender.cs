// <copyright file="IObservationSender.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>review-raven へ観測を 1 件送る.</summary>
internal interface IObservationSender
{
    /// <summary>観測を送る。失敗は例外ではなく結果で返す（呼び出し元の reviewer の起動などを止めないため）.</summary>
    /// <param name="observation">送る観測.</param>
    /// <param name="cancellationToken">キャンセル.</param>
    /// <returns>送信結果.</returns>
    Task<ObservationSendResult> SendAsync(ReviewObservation observation, CancellationToken cancellationToken);
}
