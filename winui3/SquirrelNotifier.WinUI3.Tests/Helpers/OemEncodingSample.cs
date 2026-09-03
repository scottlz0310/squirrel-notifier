// <copyright file="OemEncodingSample.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

/// <summary>
/// OEM コードページ依存のテスト用サンプル文字列を選ぶヘルパー（#231）.
/// </summary>
/// <remarks>
/// OEM コードページはロケール依存で（ja-JP: 932、en-US: 437）、表現できる非 ASCII 文字が異なる。
/// テスト側で特定のコードページを決め打ちすると、別ロケールの CI ランナーで失敗する。
/// 実行環境で「OEM として往復し、かつ UTF-8 としては不正になる」サンプルを選ぶことで、
/// フォールバック経路を確実に通しつつロケール非依存にする.
/// </remarks>
internal static class OemEncodingSample
{
    private static readonly string[] _candidates = ["日本語テスト", "café", "grüße", "señor"];

    /// <summary>
    /// 指定エンコーディングで往復し、かつ UTF-8 として不正になるサンプルを返す.
    /// </summary>
    /// <param name="oem">対象の OEM コードページのエンコーディング.</param>
    /// <returns>条件を満たすサンプル。見つからない場合は <see langword="null"/>.</returns>
    public static string? PickNonUtf8Sample(Encoding oem)
    {
        ArgumentNullException.ThrowIfNull(oem);

        foreach (string candidate in _candidates)
        {
            byte[] bytes = oem.GetBytes(candidate);
            if (oem.GetString(bytes) == candidate && !IsValidUtf8(bytes))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// バイト列が UTF-8 として妥当かどうかを判定する.
    /// </summary>
    /// <param name="bytes">判定対象のバイト列.</param>
    /// <returns>妥当な UTF-8 なら <see langword="true"/>.</returns>
    public static bool IsValidUtf8(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        try
        {
            _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
