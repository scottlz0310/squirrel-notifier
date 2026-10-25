// <copyright file="ReviewStatusChangedEventArgs.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>観測の状態が変わった、または heartbeat の時刻になったことを伝える.</summary>
/// <param name="IsHeartbeat">状態は変わらず、鮮度の更新（heartbeat）だけか.</param>
internal sealed record ReviewStatusChangedEventArgs(bool IsHeartbeat);
