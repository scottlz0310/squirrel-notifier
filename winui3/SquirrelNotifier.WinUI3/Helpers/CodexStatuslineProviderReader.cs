// <copyright file="CodexStatuslineProviderReader.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// Codex CLI のユーザー設定（<c>$CODEX_HOME/config.toml</c>、既定 <c>~/.codex/config.toml</c>）を
/// 読み取り専用で解釈し、TUI 専用カスタムステータスラインのプロバイダーとして登録された
/// MCP サーバー名を抽出する（#560）.
/// </summary>
/// <remarks>
/// Codex CLI 0.171.0+ の Custom status line はユーザー設定の <c>[mcp_servers.&lt;name&gt;]</c> と
/// <c>[tui] status_line_provider = { server = "&lt;name&gt;", tool = "..." }</c> として登録されるため、
/// 非対話の <c>codex exec</c> でも該当 MCP サーバーが起動して LLM へ不要なツールが露出する。
/// 本ヘルパーは特定ツール名をハードコードせず、実際に設定されているサーバー名だけを解決し、
/// 起動時の一時オーバーライド引数（<c>-c mcp_servers.&lt;name&gt;.enabled=false</c>）を組み立てる.
/// </remarks>
internal static class CodexStatuslineProviderReader
{
    private static readonly Regex _safeServerNameRegex = new(@"^[A-Za-z0-9_-]+$", RegexOptions.Compiled);

    /// <summary>
    /// 指定コマンドが Codex CLI（<c>codex</c> / <c>codex.exe</c> / <c>codex.cmd</c> / <c>codex.bat</c> 等）かを判定する.
    /// </summary>
    /// <param name="commandPath">launcher 設定のコマンド名または実行パス.</param>
    /// <returns>Codex CLI と判定できた場合 <see langword="true"/>.</returns>
    public static bool IsCodexCommand(string? commandPath)
    {
        if (string.IsNullOrWhiteSpace(commandPath))
        {
            return false;
        }

        string fileName = Path.GetFileName(commandPath.Trim().Trim('"'));
        string stem = Path.GetFileNameWithoutExtension(fileName);
        return string.Equals(stem, "codex", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 現在の環境の <c>$CODEX_HOME/config.toml</c>（未設定時は <c>~/.codex/config.toml</c>）から
    /// <c>[tui] status_line_provider</c> の MCP サーバー名を読み取る。
    /// ファイル不在・読み取りエラー・未設定・<c>"default"</c>・対応する <c>[mcp_servers.&lt;name&gt;]</c> 不在・
    /// 不正形式の場合は <see langword="null"/> を返す.
    /// </summary>
    /// <returns>安全な MCP サーバー名。該当なしの場合は <see langword="null"/>.</returns>
    public static string? ReadConfiguredServer()
    {
        return ReadConfiguredServer(
            Environment.GetEnvironmentVariable("CODEX_HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>
    /// 環境変数とホームディレクトリを明示して <c>config.toml</c> からサーバー名を読み取る（テスト用 DI）.
    /// </summary>
    /// <param name="codexHome">環境変数 <c>CODEX_HOME</c> の値（未設定時は <see langword="null"/> または空白）.</param>
    /// <param name="userProfile">ユーザーホームディレクトリ.</param>
    /// <returns>安全な MCP サーバー名。該当なしの場合は <see langword="null"/>.</returns>
    public static string? ReadConfiguredServer(string? codexHome, string? userProfile)
    {
        string? configPath = ResolveConfigPath(codexHome, userProfile);
        if (configPath is null || !File.Exists(configPath))
        {
            return null;
        }

        try
        {
            string content = File.ReadAllText(configPath);
            return TryExtractProviderServer(content, out string? server) ? server : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>config.toml</c> のパスを解決する.
    /// </summary>
    /// <param name="codexHome">環境変数 <c>CODEX_HOME</c> の値.</param>
    /// <param name="userProfile">ユーザーホームディレクトリ.</param>
    /// <returns>解決されたパス。どちらも空の場合は <see langword="null"/>.</returns>
    public static string? ResolveConfigPath(string? codexHome, string? userProfile)
    {
        if (!string.IsNullOrWhiteSpace(codexHome))
        {
            return Path.Combine(codexHome.Trim(), "config.toml");
        }

        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            return Path.Combine(userProfile.Trim(), ".codex", "config.toml");
        }

        return null;
    }

    /// <summary>
    /// TOML テキストを <see cref="TomlSerializer"/> で解析し、<c>[tui] status_line_provider</c> の <c>server</c> 値を抽出する。
    /// 孤立設定に対して <c>-c mcp_servers.&lt;name&gt;.enabled=false</c> を渡すと Codex CLI が
    /// <c>invalid transport</c> で起動エラーとなるため、同一設定内に <c>[mcp_servers.&lt;name&gt;]</c> が
    /// 定義されている場合のみ対象とする.
    /// </summary>
    /// <param name="tomlContent"><c>config.toml</c> の内容.</param>
    /// <param name="serverName">抽出できた安全なサーバー名.</param>
    /// <returns>有効なサーバー名が抽出できた場合 <see langword="true"/>.</returns>
    public static bool TryExtractProviderServer(string? tomlContent, out string? serverName)
    {
        serverName = null;
        if (string.IsNullOrWhiteSpace(tomlContent))
        {
            return false;
        }

        TomlTable? root;
        try
        {
            root = TomlSerializer.Deserialize<TomlTable>(tomlContent);
        }
        catch (TomlException)
        {
            return false;
        }

        if (root is null
            || !root.TryGetValue("tui", out object? tuiObj)
            || tuiObj is not TomlTable tuiTable
            || !tuiTable.TryGetValue("status_line_provider", out object? providerObj)
            || providerObj is not TomlTable providerTable
            || !providerTable.TryGetValue("server", out object? serverObj)
            || serverObj is not string extracted
            || !_safeServerNameRegex.IsMatch(extracted))
        {
            return false;
        }

        if (!root.TryGetValue("mcp_servers", out object? serversObj)
            || serversObj is not TomlTable serversTable
            || !serversTable.TryGetValue(extracted, out object? serverConfigObj)
            || serverConfigObj is not TomlTable)
        {
            return false;
        }

        serverName = extracted;
        return true;
    }

    /// <summary>
    /// 既存の起動引数へ、対象 statusline MCP サーバーを無効化するトップレベル <c>-c</c> 引数を先頭付与する。
    /// サーバー名が未設定・不正、または既存引数に同一サーバーの <c>mcp_servers.&lt;name&gt;.enabled</c> が
    /// <c>-c</c> / <c>--config</c> で既に指定されている場合は変更せず元の引数を返す.
    /// </summary>
    /// <param name="existingArgs">既存の引数リスト.</param>
    /// <param name="serverName">抑止対象の MCP サーバー名.</param>
    /// <returns>必要時のみ <c>-c mcp_servers.&lt;name&gt;.enabled=false</c> を先頭に付与した引数リスト.</returns>
    public static List<string> PrependDisableConfigArgs(IReadOnlyList<string> existingArgs, string? serverName)
    {
        ArgumentNullException.ThrowIfNull(existingArgs);

        var result = new List<string>(existingArgs.Count + 2);
        if (string.IsNullOrWhiteSpace(serverName)
            || !_safeServerNameRegex.IsMatch(serverName)
            || HasServerEnabledOverride(existingArgs, serverName))
        {
            result.AddRange(existingArgs);
            return result;
        }

        result.Add("-c");
        result.Add($"mcp_servers.{serverName}.enabled=false");
        result.AddRange(existingArgs);
        return result;
    }

    private static bool HasServerEnabledOverride(IReadOnlyList<string> arguments, string serverName)
    {
        string bareKey = $"mcp_servers.{serverName}.enabled";
        string doubleQuotedKey = $"mcp_servers.\"{serverName}\".enabled";
        string singleQuotedKey = $"mcp_servers.'{serverName}'.enabled";

        for (int i = 0; i < arguments.Count; i++)
        {
            string arg = arguments[i];
            if (arg == "--")
            {
                break;
            }

            string? configValue = null;
            if (arg is "-c" or "--config")
            {
                if (i + 1 < arguments.Count)
                {
                    configValue = arguments[++i];
                }
            }
            else if (arg.StartsWith("-c=", StringComparison.Ordinal))
            {
                configValue = arg[3..];
            }
            else if (arg.StartsWith("--config=", StringComparison.Ordinal))
            {
                configValue = arg[9..];
            }

            if (configValue is null)
            {
                continue;
            }

            int equalsIndex = configValue.IndexOf('=', StringComparison.Ordinal);
            if (equalsIndex <= 0)
            {
                continue;
            }

            string key = configValue[..equalsIndex].Trim();
            if (string.Equals(key, bareKey, StringComparison.Ordinal)
                || string.Equals(key, doubleQuotedKey, StringComparison.Ordinal)
                || string.Equals(key, singleQuotedKey, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
