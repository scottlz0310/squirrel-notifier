// <copyright file="NotificationMessage.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Models;

/// <summary>
/// トレイから表示する操作不要の通知内容.
/// </summary>
internal sealed record NotificationMessage(string Title, string Message);
