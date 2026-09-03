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
    private static readonly string[] _candidates =
    [
        "日本語テスト",
        "café",
        "grüße",
        "señor",
        "тест",
        "δοκιμή",
        "ทดสอบ",
        "測試",
        "测试",
        "테스트",
    ];

    /// <summary>
    /// 指定エンコーディングで往復し、かつ UTF-8 として不正になるサンプルを返す.
    /// </summary>
    /// <param name="oem">対象の OEM コードページのエンコーディング.</param>
    /// <returns>条件を満たすサンプル。UTF-8 や ASCII など条件を満たす文字が存在しない場合は <see langword="null"/>.</returns>
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

        return FindDynamicNonUtf8Sample(oem);
    }

    /// <summary>
    /// 対象エンコーディングのバイト空間を動的に探索し、往復可能かつ UTF-8 不正となる文字サンプルを生成する.
    /// </summary>
    /// <param name="oem">対象のエンコーディング.</param>
    /// <returns>条件を満たす文字サンプル。見つからない場合は <see langword="null"/>.</returns>
    internal static string? FindDynamicNonUtf8Sample(Encoding oem)
    {
        ArgumentNullException.ThrowIfNull(oem);

        // 1バイト探索 (0x80..0xFF)
        byte[] singleByte = new byte[1];
        for (int b = 0x80; b <= 0xFF; b++)
        {
            singleByte[0] = (byte)b;
            string text = oem.GetString(singleByte);
            if (text.Length == 1 &&
                char.IsLetter(text[0]) &&
                text[0] < 0xE000 &&
                !IsValidUtf8(singleByte))
            {
                byte[] roundtrip = oem.GetBytes(text);
                if (roundtrip.Length == 1 && roundtrip[0] == (byte)b)
                {
                    return text;
                }
            }
        }

        // 2バイト探索 (DBCS: lead 0x81..0xFE, trail 0x40..0xFE)
        byte[] doubleBytes = new byte[2];
        for (int lead = 0x81; lead <= 0xFE; lead++)
        {
            doubleBytes[0] = (byte)lead;
            for (int trail = 0x40; trail <= 0xFE; trail++)
            {
                doubleBytes[1] = (byte)trail;
                string text = oem.GetString(doubleBytes);
                if (text.Length == 1 &&
                    char.IsLetter(text[0]) &&
                    text[0] < 0xE000 &&
                    !IsValidUtf8(doubleBytes))
                {
                    byte[] roundtrip = oem.GetBytes(text);
                    if (roundtrip.Length == 2 && roundtrip[0] == (byte)lead && roundtrip[1] == (byte)trail)
                    {
                        return text;
                    }
                }
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
