// <copyright file="LaunchReviewRequest.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Models;

/// <summary>
/// トースト通知の「レビューする」ボタンから要求されたレビュー起動。
/// </summary>
internal sealed record LaunchReviewRequest(string EventId, LauncherRole Role);
