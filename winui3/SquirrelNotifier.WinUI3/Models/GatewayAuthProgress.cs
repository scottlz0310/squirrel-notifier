// <copyright file="GatewayAuthProgress.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Models;

/// <summary>
/// mcp-gateway 認証（<c>mcp-resource-subscriber --login</c>）の進行状態ステージ.
/// </summary>
internal enum GatewayAuthStage
{
    /// <summary>
    /// 未開始.
    /// </summary>
    NotStarted,

    /// <summary>
    /// プロセス起動中・URL 抽出待ち.
    /// </summary>
    Starting,

    /// <summary>
    /// Verification URL / User Code 検出完了・ユーザー操作待ち.
    /// </summary>
    WaitingForUser,

    /// <summary>
    /// 認証成功 (ExitCode 0).
    /// </summary>
    Success,

    /// <summary>
    /// 認証失敗 (ExitCode 非0 またはエラー).
    /// </summary>
    Failed,

    /// <summary>
    /// キャンセル済み.
    /// </summary>
    Cancelled,

    /// <summary>
    /// タイムアウト.
    /// </summary>
    Timeout,
}

/// <summary>
/// mcp-gateway 認証の進行状況情報.
/// </summary>
internal sealed class GatewayAuthProgress
{
    /// <summary>
    /// 現在の進行ステージ.
    /// </summary>
    public GatewayAuthStage Stage { get; set; } = GatewayAuthStage.NotStarted;

    /// <summary>
    /// 検出された verification URL.
    /// </summary>
    public string? VerificationUrl { get; set; }

    /// <summary>
    /// 検出された User Code（存在する場合）.
    /// </summary>
    public string? UserCode { get; set; }

    /// <summary>
    /// 既定ブラウザの起動に失敗したかどうか.
    /// </summary>
    public bool BrowserLaunchFailed { get; set; }

    /// <summary>
    /// エラー詳細メッセージ（失敗時）.
    /// </summary>
    public string? ErrorMessage { get; set; }
}
