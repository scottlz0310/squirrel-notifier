// <copyright file="LauncherWorkingDirectoryResolver.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// launcher role ごとの作業ディレクトリ契約を解決する（#186）.
/// </summary>
internal sealed class LauncherWorkingDirectoryResolver
{
    private readonly SettingsService _settingsService;

    public LauncherWorkingDirectoryResolver(SettingsService settingsService)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        _settingsService = settingsService;
    }

    public string Resolve(ReviewEvent reviewEvent, LauncherRole role)
    {
        ArgumentNullException.ThrowIfNull(reviewEvent);

        if (role == LauncherRole.Reviewer)
        {
            string reviewerDirectory = Path.Combine(_settingsService.SettingsDirectory, "launcher-workspace", "reviewer");
            Directory.CreateDirectory(reviewerDirectory);
            return Path.GetFullPath(reviewerDirectory);
        }

        string? configuredPath = _settingsService.ResolveRepositoryCheckoutPath(reviewEvent.Repository);
        if (configuredPath is null)
        {
            throw new InvalidOperationException(
                $"{reviewEvent.Repository} のローカル checkout mapping がありません。Settings の Checkout Mappings に owner/repo=絶対パス を設定してください。");
        }

        string fullPath = Path.GetFullPath(configuredPath);
        if (!Directory.Exists(fullPath))
        {
            throw new InvalidOperationException($"{reviewEvent.Repository} の checkout directory が存在しません: {fullPath}");
        }

        if (IsProtectedDirectory(fullPath))
        {
            throw new InvalidOperationException($"システムまたはインストール先ディレクトリは checkout mapping に使用できません: {fullPath}");
        }

        string gitMarker = Path.Combine(fullPath, ".git");
        if (!Directory.Exists(gitMarker) && !File.Exists(gitMarker))
        {
            throw new InvalidOperationException($"{reviewEvent.Repository} の mapping は Git checkout ではありません: {fullPath}");
        }

        return fullPath;
    }

    private static bool IsProtectedDirectory(string path)
    {
        string[] protectedRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            AppContext.BaseDirectory,
        ];

        return protectedRoots
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Any(root => IsSameOrDescendant(path, root));
    }

    private static bool IsSameOrDescendant(string path, string root)
    {
        string relativePath = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relativePath == "."
            || (!relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && relativePath != ".."
                && !Path.IsPathFullyQualified(relativePath));
    }
}
