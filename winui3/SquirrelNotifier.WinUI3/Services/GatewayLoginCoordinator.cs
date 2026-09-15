// <copyright file="GatewayLoginCoordinator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>ログイン開始要求の判定結果（#265）.</summary>
internal enum GatewayLoginStartStatus
{
    /// <summary>ログインを開始してよい.</summary>
    Started,

    /// <summary>ログインが進行中のため見送った（ボタン連打などによる再入）.</summary>
    SkippedReentrant,

    /// <summary>Gateway URL が未設定または http(s) 形式でない.</summary>
    InvalidGatewayUrl,
}

/// <summary>
/// ログイン開始の可否と、開始できないときにユーザーへ伝える内容。
/// <see cref="ErrorTitle"/> が <see langword="null"/> の場合は何も表示しない.
/// </summary>
internal sealed record GatewayLoginStartDecision(
    GatewayLoginStartStatus Status,
    string? ErrorTitle,
    string? ErrorMessage)
{
    public bool CanStart => Status == GatewayLoginStartStatus.Started;
}

/// <summary>
/// device flow の承認情報のうち、ダイアログに出す値。<see cref="UserCode"/> が
/// <see langword="null"/> のときはコード欄そのものを出さない（subscriber が code を返さない構成があるため）.
/// </summary>
internal sealed record DeviceVerificationView(string Url, string? UserCode)
{
    public bool HasUserCode => UserCode is not null;
}

/// <summary>
/// ログイン完了後に UI が行うこと。<see cref="DialogTitle"/> が <see langword="null"/> の
/// 場合はダイアログを出さない（ユーザー操作によるキャンセル）.
/// </summary>
internal sealed record GatewayLoginPresentation(
    bool CloseAuthRequiredInfoBar,
    bool RestartSubscription,
    string? DialogTitle,
    string? DialogMessage)
{
    public bool HasDialog => DialogTitle is not null;
}

/// <summary>
/// mcp-gateway の device flow login（#183）における進行状態と結果解釈を担う（#265）。
/// 多重ログインの抑止・Gateway URL の検証・<see cref="McpLoginResult"/> から表示内容への変換を持ち、
/// <c>ContentDialog</c> の生成・クリップボード操作・<c>InfoBar</c> の開閉は呼び出し側に残す。
/// 認証処理そのものは <see cref="McpLoginService"/> の責務であり、本クラスは関与しない.
/// </summary>
/// <remarks>進行中フラグを持つためスレッドセーフではない。UI スレッドからの利用を前提とする.</remarks>
internal sealed class GatewayLoginCoordinator
{
    private const string _invalidUrlTitle = "設定エラー";
    private const string _invalidUrlMessage = "Gateway URL が正しくありません。先に Gateway URL を http(s):// 形式で設定してください。";

    private bool _isLoginPending;

    /// <summary>Gets a value indicating whether ログインが進行中か.</summary>
    public bool IsLoginPending => _isLoginPending;

    /// <summary>
    /// device flow の承認情報から、ダイアログに出す値を組み立てる.
    /// </summary>
    /// <param name="info">subscriber から受信した承認情報.</param>
    /// <returns>表示する URL と user code.</returns>
    public static DeviceVerificationView DescribeVerification(DeviceVerificationInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return new DeviceVerificationView(
            info.DisplayUri,
            string.IsNullOrEmpty(info.UserCode) ? null : info.UserCode);
    }

    /// <summary>
    /// ログイン結果を、UI が行うことへ変換する.
    /// </summary>
    /// <param name="result">login の最終結果.</param>
    /// <param name="subscriptionState">ログイン完了時点の購読状態。成功時の再購読要否の判定に使う.</param>
    /// <returns>InfoBar・購読・ダイアログに対する指示.</returns>
    public static GatewayLoginPresentation DescribeResult(McpLoginResult result, SubscriptionState subscriptionState)
    {
        ArgumentNullException.ThrowIfNull(result);

        switch (result.Outcome)
        {
            case McpLoginOutcome.Succeeded:
                // 認証成功後、購読が停止中または Error なら再購読を開始し、手動レビュー開始を
                // 再試行できる状態へ戻す（#183 AC）
                bool restart = subscriptionState is SubscriptionState.Stopped or SubscriptionState.Error;
                string message = "mcp-gateway への認証に成功しました。";
                if (restart)
                {
                    message += "\n購読を再開しました。";
                }

                return new GatewayLoginPresentation(true, restart, "ログイン成功", message);

            case McpLoginOutcome.Cancelled:
                // ユーザー操作による中断のため、追加の通知は出さない
                return new GatewayLoginPresentation(false, false, null, null);

            case McpLoginOutcome.TimedOut:
                return new GatewayLoginPresentation(
                    false,
                    false,
                    "ログインがタイムアウトしました",
                    result.ErrorMessage ?? "認証が時間内に完了しませんでした。");

            case McpLoginOutcome.Failed:
            default:
                return new GatewayLoginPresentation(
                    false,
                    false,
                    "ログインに失敗しました",
                    result.ErrorMessage ?? "mcp-gateway へのログインに失敗しました。");
        }
    }

    /// <summary>
    /// ログインを開始してよいかを判定し、開始できる場合は進行中としてマークする.
    /// </summary>
    /// <param name="gatewayUrl">設定欄に入力されている Gateway URL.</param>
    /// <returns>開始可否と、開始できない場合に表示する内容.</returns>
    public GatewayLoginStartDecision TryBeginLogin(string? gatewayUrl)
    {
        // 再入はダイアログを出さず黙って無視する。ログインダイアログは 1 つしか開けない
        if (_isLoginPending)
        {
            return new GatewayLoginStartDecision(GatewayLoginStartStatus.SkippedReentrant, null, null);
        }

        if (!IsSupportedGatewayUrl(gatewayUrl))
        {
            return new GatewayLoginStartDecision(
                GatewayLoginStartStatus.InvalidGatewayUrl,
                _invalidUrlTitle,
                _invalidUrlMessage);
        }

        _isLoginPending = true;
        return new GatewayLoginStartDecision(GatewayLoginStartStatus.Started, null, null);
    }

    /// <summary>ログインの進行を終了する。<see cref="TryBeginLogin"/> が成功した呼び出しの finally で呼ぶ.</summary>
    public void EndLogin() => _isLoginPending = false;

    private static bool IsSupportedGatewayUrl(string? gatewayUrl)
        => !string.IsNullOrWhiteSpace(gatewayUrl)
            && Uri.TryCreate(gatewayUrl, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
