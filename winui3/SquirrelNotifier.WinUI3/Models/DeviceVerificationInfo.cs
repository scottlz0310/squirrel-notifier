// <copyright file="DeviceVerificationInfo.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Models;

/// <summary>
/// device flow の承認情報。ブラウザ起動に失敗しても UI から手動で開けるよう、
/// verification URI と user code を保持する（#183）。永続化・通常ログには残さない.
/// </summary>
internal sealed class DeviceVerificationInfo
{
    /// <summary>Gets ブラウザで開く承認 URI（code 未入力）.</summary>
    public string VerificationUri { get; init; } = string.Empty;

    /// <summary>Gets code 事前入力済みの承認 URI（subscriber が提供した場合のみ）.</summary>
    public string? VerificationUriComplete { get; init; }

    /// <summary>Gets ユーザーがブラウザで入力する user code.</summary>
    public string UserCode { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether 既定ブラウザの起動に成功したか.</summary>
    public bool BrowserOpened { get; init; }

    /// <summary>Gets UI 表示・コピーに用いる推奨 URI（complete があればそちらを優先）.</summary>
    public string DisplayUri => string.IsNullOrWhiteSpace(VerificationUriComplete)
        ? VerificationUri
        : VerificationUriComplete!;
}
