// <copyright file="IAutoStartService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// ログオン時の自動起動の操作。配布方式で仕組みが異なる（MSI 版はタスクスケジューラ、MSIX 版はスタートアップタスク）.
/// </summary>
internal interface IAutoStartService
{
    /// <summary>Gets 有効化の前に確認ダイアログへ表示する本文.</summary>
    string EnableConfirmationMessage { get; }

    /// <summary>Gets 無効化の前に確認ダイアログへ表示する本文.</summary>
    string DisableConfirmationMessage { get; }

    Task<AutoStartStatus> GetStatusAsync();

    Task EnableAsync();

    Task DisableAsync();
}

/// <summary>登録内容が現在のアプリとずれることがあり、登録し直せる自動起動.</summary>
internal interface IRepairableAutoStartService : IAutoStartService
{
    Task RepairAsync();
}

internal enum AutoStartStatus
{
    /// <summary>自動起動は無効で、アプリから有効にできる.</summary>
    Disabled,

    /// <summary>自動起動は有効.</summary>
    Enabled,

    /// <summary>登録はあるが、内容が現在のアプリと一致しない.</summary>
    Invalid,

    /// <summary>利用者が Windows の設定で無効にしていて、アプリからは有効にできない.</summary>
    DisabledByUser,

    /// <summary>管理者のポリシーで無効.</summary>
    DisabledByPolicy,

    /// <summary>管理者のポリシーで有効.</summary>
    EnabledByPolicy,

    /// <summary>状態を取得できなかった.</summary>
    CheckFailed,
}
