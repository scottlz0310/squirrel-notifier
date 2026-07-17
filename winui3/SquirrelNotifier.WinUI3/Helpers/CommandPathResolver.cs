// <copyright file="CommandPathResolver.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// PATH / PATHEXT を自前で解決し、コマンドの実体（フルパス）を探す共通 resolver（#186）。
/// Win32 <c>CreateProcessW</c> は拡張子省略時に <c>.exe</c> のみを暗黙補完し、シェル側の機能である
/// PATHEXT 解決（<c>.cmd</c> / <c>.bat</c> 等）は行わない。npm / pnpm 経由でインストールされた
/// CLI は Windows 上では <c>.cmd</c> シムであることが多く、ターミナルでは実行できるのに
/// プロセス起動だけ <c>ERROR_FILE_NOT_FOUND</c> になる（#177）。エージェント起動経路ごとに
/// 解決規約が分岐しないよう、レートリミット取得・レビュー起動の両方がここを使う.
/// </summary>
internal static class CommandPathResolver
{
    private const string _defaultPathExt = ".COM;.EXE;.BAT;.CMD";

    /// <summary>
    /// コマンド名を実体のフルパスへ解決する。直接パス指定（存在するファイル）を最優先し、
    /// 次に PATH の各ディレクトリを PATHEXT の優先順で探索、最後に追加ディレクトリ
    /// （pnpm global bin 等）を探索する.
    /// </summary>
    /// <param name="command">コマンド名または既存ファイルへのパス.</param>
    /// <param name="extraSearchDirectory">PATH の後に探索する追加ディレクトリ（pnpm global bin 等）.</param>
    /// <param name="pathVariable">テスト用の PATH 上書き。<see langword="null"/> で環境変数を使用.</param>
    /// <param name="pathExtVariable">テスト用の PATHEXT 上書き。<see langword="null"/> で環境変数を使用.</param>
    /// <returns>解決できた場合はフルパス。見つからない場合は <see langword="null"/>.</returns>
    public static string? Resolve(
        string command,
        string? extraSearchDirectory = null,
        string? pathVariable = null,
        string? pathExtVariable = null)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        if (File.Exists(command))
        {
            return Path.GetFullPath(command);
        }

        string[] extensions = OperatingSystem.IsWindows()
            ? (pathExtVariable ?? Environment.GetEnvironmentVariable("PATHEXT") ?? _defaultPathExt)
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [];

        pathVariable ??= Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        List<string> directories = new(pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        if (!string.IsNullOrEmpty(extraSearchDirectory))
        {
            directories.Add(extraSearchDirectory);
        }

        foreach (string directory in directories)
        {
            string basePath = Path.Combine(directory.Trim(), command);
            foreach (string extension in extensions)
            {
                string candidate = basePath + extension;
                if (File.Exists(candidate))
                {
                    // PATHEXT は大文字（.CMD 等）で列挙されるため、ディスク上の実ファイル名の
                    // 大小文字へ正規化して返す（設定へ永続化されるパスの見た目を安定させる）
                    return ToActualCasePath(candidate);
                }
            }

            // コマンド名が拡張子込みで指定された場合（例: "codex.cmd"）の完全一致
            if (File.Exists(basePath))
            {
                return Path.GetFullPath(basePath);
            }
        }

        return null;
    }

    private static string ToActualCasePath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            return fullPath;
        }

        return Directory.EnumerateFiles(directory, Path.GetFileName(fullPath)).FirstOrDefault() ?? fullPath;
    }
}
