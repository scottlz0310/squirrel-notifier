// <copyright file="DeviceLoginOutputParser.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// mcp-resource-subscriber <c>--login</c>（RFC 8628 device flow）の line-based stdout を
/// 1 行ずつ分類する（#183）。subscriber は認証状態を以下の行で通知する:
/// <list type="bullet">
/// <item><c>user-code &lt;code&gt;</c></item>
/// <item><c>verification-uri &lt;uri&gt;</c></item>
/// <item><c>verification-uri-complete &lt;uri&gt;</c>（任意。code 事前入力済み URI）</item>
/// <item><c>login-status success</c> / <c>login-status failed</c></item>
/// <item><c>error-code &lt;code&gt;</c></item>
/// </list>
/// アクセストークン・リフレッシュトークンは subscriber が stdout / stderr に一切出力しないため、
/// この parser がトークンを扱うことはない.
/// </summary>
internal static class DeviceLoginOutputParser
{
    private const string _userCodePrefix = "user-code ";
    private const string _verificationUriCompletePrefix = "verification-uri-complete ";
    private const string _verificationUriPrefix = "verification-uri ";
    private const string _errorCodePrefix = "error-code ";
    private const string _statusSuccess = "login-status success";
    private const string _statusFailed = "login-status failed";

    public static DeviceLoginSignal Parse(string line)
    {
        if (line is null)
        {
            return new DeviceLoginSignal(DeviceLoginSignalKind.Ignored, string.Empty);
        }

        string trimmed = line.Trim();

        if (trimmed.Equals(_statusSuccess, StringComparison.Ordinal))
        {
            return new DeviceLoginSignal(DeviceLoginSignalKind.StatusSuccess, string.Empty);
        }

        if (trimmed.Equals(_statusFailed, StringComparison.Ordinal))
        {
            return new DeviceLoginSignal(DeviceLoginSignalKind.StatusFailed, string.Empty);
        }

        // verification-uri-complete は verification-uri の接頭辞を包含するため先に判定する。
        if (TryStripPrefix(trimmed, _verificationUriCompletePrefix, out string completeUri))
        {
            return new DeviceLoginSignal(DeviceLoginSignalKind.VerificationUriComplete, completeUri);
        }

        if (TryStripPrefix(trimmed, _verificationUriPrefix, out string verificationUri))
        {
            return new DeviceLoginSignal(DeviceLoginSignalKind.VerificationUri, verificationUri);
        }

        if (TryStripPrefix(trimmed, _userCodePrefix, out string userCode))
        {
            return new DeviceLoginSignal(DeviceLoginSignalKind.UserCode, userCode);
        }

        if (TryStripPrefix(trimmed, _errorCodePrefix, out string errorCode))
        {
            return new DeviceLoginSignal(DeviceLoginSignalKind.ErrorCode, errorCode);
        }

        return new DeviceLoginSignal(DeviceLoginSignalKind.Ignored, string.Empty);
    }

    private static bool TryStripPrefix(string line, string prefix, out string value)
    {
        if (line.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = line[prefix.Length..].Trim();
            return value.Length > 0;
        }

        value = string.Empty;
        return false;
    }
}

/// <summary>
/// <see cref="DeviceLoginOutputParser"/> が 1 行から抽出した意味づけ.
/// </summary>
internal enum DeviceLoginSignalKind
{
    /// <summary>認証に無関係な行、または値を持たない行.</summary>
    Ignored,

    /// <summary><c>user-code</c>: ユーザーがブラウザで入力するコード.</summary>
    UserCode,

    /// <summary><c>verification-uri</c>: ブラウザで開く承認 URI（code 未入力）.</summary>
    VerificationUri,

    /// <summary><c>verification-uri-complete</c>: code 事前入力済みの承認 URI.</summary>
    VerificationUriComplete,

    /// <summary><c>login-status success</c>: 認証成功.</summary>
    StatusSuccess,

    /// <summary><c>login-status failed</c>: 認証失敗.</summary>
    StatusFailed,

    /// <summary><c>error-code</c>: 失敗時の構造化エラーコード.</summary>
    ErrorCode,
}

/// <summary>
/// 分類済みの 1 行.
/// </summary>
/// <param name="Kind">行の種別.</param>
/// <param name="Value">付随する値（種別に値が無い場合は空文字列）.</param>
internal readonly record struct DeviceLoginSignal(DeviceLoginSignalKind Kind, string Value);
