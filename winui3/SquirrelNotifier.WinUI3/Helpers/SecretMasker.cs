// <copyright file="SecretMasker.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// ログ表示前に機密値をマスクする（#144）。マスキング対象は以下の 2 種類に明示的に限定し、
/// このルール外の機密値は保護対象外とする（詳細は docs/live-log-window.md）:
/// (a) 既知トークン形式のパターン（GitHub PAT / sk- 系 API キー / Bearer ヘッダ）、
/// (b) squirrel-notifier 自身が参照する認証情報（MCP_PROBE_AUTH_TOKEN）の値と一致する文字列.
/// </summary>
internal sealed class SecretMasker
{
    private const string _mask = "***";

    // (a) 既知トークン形式のパターン。誤検出よりも取りこぼしを避ける安全側の長さ下限にする。
    // 順に: GitHub classic / OAuth / installation トークン（gh[pousr]_）、GitHub fine-grained PAT、
    // sk- 形式 API キー（OpenAI / Anthropic。sk-ant- / sk-proj- を包含）、Bearer ヘッダのトークン部
    private static readonly Regex[] _knownTokenPatterns =
    [
        new(@"\bgh[pousr]_[A-Za-z0-9]{16,}\b", RegexOptions.Compiled),
        new(@"\bgithub_pat_[A-Za-z0-9_]{22,}\b", RegexOptions.Compiled),
        new(@"\bsk-[A-Za-z0-9_\-]{16,}\b", RegexOptions.Compiled),
        new(@"\bBearer\s+[A-Za-z0-9._+/=\-]{8,}", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    ];

    // (b) squirrel-notifier 自身が保持する認証情報の実値（長い順に照合し部分マスクの重複を避ける）
    private readonly string[] _knownSecrets;

    public SecretMasker(IEnumerable<string?> knownSecrets)
    {
        ArgumentNullException.ThrowIfNull(knownSecrets);
        _knownSecrets = knownSecrets
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(s => s.Length)
            .ToArray();
    }

    /// <summary>
    /// プロセス環境から squirrel-notifier が参照する認証情報を収集した既定のマスカーを生成する.
    /// </summary>
    /// <returns>既定の <see cref="SecretMasker"/>.</returns>
    public static SecretMasker CreateDefault()
        => new([Environment.GetEnvironmentVariable("MCP_PROBE_AUTH_TOKEN")]);

    /// <summary>
    /// マスキングルールに合致する文字列を <c>***</c> へ置換する.
    /// </summary>
    /// <param name="text">対象テキスト（ログ 1 行）.</param>
    /// <returns>マスク後のテキスト.</returns>
    public string Mask(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string masked = text;
        foreach (string secret in _knownSecrets)
        {
            masked = masked.Replace(secret, _mask, StringComparison.Ordinal);
        }

        foreach (Regex pattern in _knownTokenPatterns)
        {
            masked = pattern.Replace(masked, static m =>
                m.Value.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase) ? $"Bearer {_mask}" : _mask);
        }

        return masked;
    }
}
