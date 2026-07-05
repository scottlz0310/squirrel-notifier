// <copyright file="IRateLimitReminderService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;

namespace SquirrelNotifier.WinUI3.Services;

internal interface IRateLimitReminderService
{
    bool IsScheduled(string id);

    void Schedule(string id, string label, DateTimeOffset resetAt);

    void Cancel(string id);

    // タイマー満了で通知を出した（= 予約が消費された）ことを呼び出し元に伝える。
    // 引数は Schedule/Cancel に渡した id。UI 側はこれを使って表示状態を同期する。
    event EventHandler<string>? ReminderFired;
}
