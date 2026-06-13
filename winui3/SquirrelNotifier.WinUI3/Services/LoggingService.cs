// <copyright file="LoggingService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.IO;

namespace SquirrelNotifier.WinUI3.Services;

internal sealed class LoggingService
{
    private const long _defaultMaxBytes = 1_000_000; // 1MB
    private readonly string _logDirectory;
    private readonly string _logFilePath;
    private readonly long _maxBytes;

    public event EventHandler<string>? LogAppended;

    public LoggingService()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SquirrelNotifier", "logs"), _defaultMaxBytes)
    {
    }

    internal LoggingService(string logDirectory, long maxBytes = _defaultMaxBytes)
    {
        if (string.IsNullOrWhiteSpace(logDirectory))
        {
            throw new ArgumentException("ログの出力先ディレクトリが不正です。", nameof(logDirectory));
        }

        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "ログファイルの最大サイズは 1 バイト以上である必要があります。");
        }

        _logDirectory = logDirectory;
        _maxBytes = maxBytes;
        Directory.CreateDirectory(_logDirectory);
        _logFilePath = Path.Combine(_logDirectory, "winui3.log");
    }

    public string LogDirectory => _logDirectory;

    public async Task WriteAsync(string message)
    {
        string line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        await RotateIfNeededAsync().ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(_logFilePath, line + Environment.NewLine).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to write log: {ex.Message}");
        }

        LogAppended?.Invoke(this, line);
    }

    private Task RotateIfNeededAsync()
    {
        try
        {
            var info = new FileInfo(_logFilePath);
            if (info.Exists && info.Length > _maxBytes)
            {
                string archive = Path.Combine(_logDirectory, $"winui3-{DateTimeOffset.Now:yyyyMMddHHmmss}.log");
                File.Move(_logFilePath, archive, true);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to rotate log: {ex.Message}");
        }

        return Task.CompletedTask;
    }
}
