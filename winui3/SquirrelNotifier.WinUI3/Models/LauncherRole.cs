// <copyright file="LauncherRole.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Models;

/// <summary>
/// レビュー起動に使うランチャースロット。ユーザーが押したアクション
/// （レビューする / レビューに対応）で決まり、グローバル設定は持たない.
/// </summary>
internal enum LauncherRole
{
    /// <summary>reviewer 側（自分がレビューする側）のランチャースロット.</summary>
    Reviewer,

    /// <summary>reviewed 側（レビューを受ける側）のランチャースロット.</summary>
    Reviewed,
}
