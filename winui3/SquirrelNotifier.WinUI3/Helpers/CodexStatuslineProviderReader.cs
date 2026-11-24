// <copyright file="CodexStatuslineProviderReader.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

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
    private static readonly Regex _inlineServerRegex = new(
        @"(?:^|[,\{\s])server\s*=\s*(?:""([^""\r\n]+)""|'([^'\r\n]+)')",
        RegexOptions.Compiled);

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
    /// ファイル不在・読み取りエラー・未設定・<c>"default"</c>・不正形式の場合は <see langword="null"/> を返す.
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
    /// TOML テキストから <c>[tui] status_line_provider</c>（インラインテーブル形式および
    /// <c>[tui.status_line_provider]</c> サブテーブル形式の両方）の <c>server</c> 値を抽出する.
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

        string currentSection = string.Empty;
        string? extracted = null;

        using var reader = new StringReader(tomlContent);
        while (reader.ReadLine() is string rawLine)
        {
            string line = StripTomlComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('[', StringComparison.Ordinal)
                && line.EndsWith(']', StringComparison.Ordinal)
                && !line.StartsWith("[[", StringComparison.Ordinal))
            {
                currentSection = line[1..^1].Trim();
                continue;
            }

            if (string.Equals(currentSection, "tui", StringComparison.Ordinal))
            {
                if (TrySplitKeyValue(line, out string key, out string value)
                    && string.Equals(key, "status_line_provider", StringComparison.Ordinal))
                {
                    extracted = TryExtractInlineTableServer(value);
                }
            }
            else if (string.Equals(currentSection, "tui.status_line_provider", StringComparison.Ordinal))
            {
                if (TrySplitKeyValue(line, out string key, out string value)
                    && string.Equals(key, "server", StringComparison.Ordinal))
                {
                    extracted = TryUnquoteTomlString(value);
                }
            }
        }

        if (extracted is null || !_safeServerNameRegex.IsMatch(extracted))
        {
            return false;
        }

        serverName = extracted;
        return true;
    }

    /// <summary>
    /// 既存の起動引数へ、対象 statusline MCP サーバーを無効化するトップレベル <c>-c</c> 引数を先頭付与する。
    /// サーバー名が未設定・不正、または既存引数に同一サーバーの <c>mcp_servers.&lt;name&gt;.enabled=</c> が
    /// 既に含まれる場合は変更せず元の引数を返す.
    /// </summary>
    /// <param name="existingArgs">既存の引数リスト.</param>
    /// <param name="serverName">抑止対象の MCP サーバー名.</param>
    /// <returns>必要時のみ <c>-c mcp_servers.&lt;name&gt;.enabled=false</c> を先頭に付与した引数リスト.</returns>
    public static List<string> PrependDisableConfigArgs(IReadOnlyList<string> existingArgs, string? serverName)
    {
        ArgumentNullException.ThrowIfNull(existingArgs);

        var result = new List<string>(existingArgs.Count + 2);
        if (string.IsNullOrWhiteSpace(serverName) || !_safeServerNameRegex.IsMatch(serverName))
        {
            result.AddRange(existingArgs);
            return result;
        }

        string keyPrefix = $"mcp_servers.{serverName}.enabled=";
        string quotedKeyPrefix = $"mcp_servers.\"{serverName}\".enabled=";
        foreach (string arg in existingArgs)
        {
            if (arg.StartsWith(keyPrefix, StringComparison.Ordinal)
                || arg.StartsWith(quotedKeyPrefix, StringComparison.Ordinal))
            {
                result.AddRange(existingArgs);
                return result;
            }
        }

        result.Add("-c");
        result.Add($"mcp_servers.{serverName}.enabled=false");
        result.AddRange(existingArgs);
        return result;
    }

    private static string? TryExtractInlineTableServer(string value)
    {
        string trimmed = value.Trim();
        if (!trimmed.StartsWith('{', StringComparison.Ordinal) || !trimmed.EndsWith('}', StringComparison.Ordinal))
        {
            // "default" や不正形式は抑止対象なしとして扱う
            return null;
        }

        Match match = _inlineServerRegex.Match(trimmed);
        if (!match.Success)
        {
            return null;
        }

        return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
    }

    private static string? TryUnquoteTomlString(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length < 2)
        {
            return null;
        }

        char quote = trimmed[0];
        if ((quote == '"' || quote == '\'') && trimmed[^1] == quote)
        {
            return trimmed[1..^1];
        }

        return null;
    }

    private static bool TrySplitKeyValue(string line, out string key, out string value)
    {
        int eqIndex = line.IndexOf('=', StringComparison.Ordinal);
        if (eqIndex <= 0)
        {
            key = string.Empty;
            value = string.Empty;
            return false;
        }

        key = line[..eqIndex].Trim();
        value = line[(eqIndex + 1)..].Trim();
        return key.Length > 0 && value.Length > 0;
    }

    // 引用符の外側の '#' 以降をコメントとして取り除く
    private static string StripTomlComment(string line)
    {
        bool inDouble = false;
        bool inSingle = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\' && inDouble && i + 1 < line.Length)
            {
                i++;
                continue;
            }

            if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
            }
            else if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
            }
            else if (c == '#' && !inDouble && !inSingle)
            {
                return line[..i];
            }
        }

        return line;
    }
}
