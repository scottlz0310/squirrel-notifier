// <copyright file="CacheService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text.Json;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

internal sealed class CacheService : ICacheService
{
    private readonly string _cachePath;
    private readonly string _tempPath;

    public CacheService()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SquirrelNotifier"))
    {
    }

    internal CacheService(string cacheDirectory)
    {
        if (string.IsNullOrWhiteSpace(cacheDirectory))
        {
            throw new ArgumentException("キャッシュ保存先のディレクトリが不正です。", nameof(cacheDirectory));
        }

        Directory.CreateDirectory(cacheDirectory);
        _cachePath = Path.Combine(cacheDirectory, "cache.json");
        _tempPath = _cachePath + ".tmp";
    }

    public async Task<NotificationCache> LoadAsync()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return new NotificationCache();
            }

            string json = await File.ReadAllTextAsync(_cachePath).ConfigureAwait(false);
            NotificationCache result = JsonSerializer.Deserialize<NotificationCache>(json) ?? new NotificationCache();
            result.SeenEventIds ??= [];
            result.RecentEvents ??= [];
            return result;
        }
        catch
        {
            return new NotificationCache();
        }
    }

    public async Task SaveAsync(NotificationCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);

        string json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_tempPath, json).ConfigureAwait(false);

        // アトミック置換: 書き込み完了後にのみ既存ファイルを上書き
        File.Move(_tempPath, _cachePath, overwrite: true);
    }
}
