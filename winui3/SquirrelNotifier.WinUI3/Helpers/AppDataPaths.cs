// <copyright file="AppDataPaths.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// 設定・ログ・キャッシュなどを置くデータディレクトリの root を決める（#433）.
/// </summary>
internal static class AppDataPaths
{
    /// <summary>
    /// データディレクトリの root を上書きする環境変数名. 未設定なら %LocalAppData%\SquirrelNotifier を使う.
    /// </summary>
    public const string DataRootEnvironmentVariable = "SQUIRREL_NOTIFIER_DATA_ROOT";

    private const string _defaultDirectoryName = "SquirrelNotifier";
    private const string _logDirectoryName = "logs";

    public static string Root => ResolveRoot(
        Environment.GetEnvironmentVariable(DataRootEnvironmentVariable),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public static string LogDirectory => Path.Combine(Root, _logDirectoryName);

    public static string SingleInstanceKey(string baseKey)
        => ResolveSingleInstanceKey(
            baseKey,
            Environment.GetEnvironmentVariable(DataRootEnvironmentVariable),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal static string ResolveRoot(string? overrideRoot, string localApplicationData)
    {
        if (string.IsNullOrWhiteSpace(overrideRoot))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);
            return Path.Combine(localApplicationData, _defaultDirectoryName);
        }

        // 相対パスは起動時のカレントディレクトリで解決先が変わり、利用者の実データと取り違えうるため受け付けない
        if (!Path.IsPathFullyQualified(overrideRoot))
        {
            throw new ArgumentException(
                $"環境変数 {DataRootEnvironmentVariable} は絶対パスで指定してください: '{overrideRoot}'",
                nameof(overrideRoot));
        }

        return Path.GetFullPath(overrideRoot);
    }

    // 上書き時は root ごとに別インスタンスとして扱い、利用者が常駐させている既定のインスタンスへ
    // 起動がリダイレクトされないようにする. 既定の root では従来のキーを変えない.
    // 既定と同じ root を明示した場合も従来のキーにそろえ、同じデータを 2 プロセスが共有しないようにする.
    internal static string ResolveSingleInstanceKey(string baseKey, string? overrideRoot, string localApplicationData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseKey);
        if (string.IsNullOrWhiteSpace(overrideRoot))
        {
            return baseKey;
        }

        string root = NormalizeForComparison(ResolveRoot(overrideRoot, localApplicationData));
        if (root == NormalizeForComparison(ResolveRoot(overrideRoot: null, localApplicationData)))
        {
            return baseKey;
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(root));
        return string.Create(CultureInfo.InvariantCulture, $"{baseKey}-{Convert.ToHexStringLower(hash)[..16]}");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308", Justification = "Windows のパスは大文字小文字を区別しないため、同じ root を同じ値へ正規化する")]
    private static string NormalizeForComparison(string root)
        => root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
}
