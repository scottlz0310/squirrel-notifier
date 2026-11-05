// <copyright file="ReviewerOutputRecorder.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SquirrelNotifier.WinUI3.Helpers;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// reviewer プロセス 1 回の起動分の標準出力・標準エラーを、受信時刻つきでファイルへ記録する.
/// 実行ウィンドウの表示（要約済み）とは別に、後から停止原因を調べるための生の記録を残す.
/// 記録の失敗はレビュー自体を止めない（診断用の副経路のため）。失敗は <see cref="FailureMessage"/> に残る.
/// </summary>
internal sealed class ReviewerOutputRecorder : IDisposable
{
    /// <summary>1 ファイルの上限。超えた分は記録せず、その旨を 1 行だけ残す.</summary>
    public const long MaxFileBytes = 32L * 1024 * 1024;

    /// <summary>保存先全体の上限。超えた場合は古いファイルから削除する.</summary>
    public const long MaxTotalBytes = 256L * 1024 * 1024;

    /// <summary>この期間を過ぎたファイルは削除する.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    private readonly StreamWriter _writer;
    private readonly SecretMasker _masker;
    private readonly TimeProvider _timeProvider;
    private readonly long _maxFileBytes;
    private readonly object _gate = new();

    private long _written;
    private bool _stopped;

    private ReviewerOutputRecorder(string path, StreamWriter writer, SecretMasker masker, TimeProvider timeProvider, long maxFileBytes)
    {
        FilePath = path;
        _writer = writer;
        _masker = masker;
        _timeProvider = timeProvider;
        _maxFileBytes = maxFileBytes;
    }

    public string FilePath { get; }

    // 記録中に書き込みが失敗した場合の理由。失敗していなければ null.
    public string? FailureMessage { get; private set; }

    // 既存のファイルは上書きしない（CreateNew）。他のプロセスが tail できるよう読み取り共有で開く.
    public static ReviewerOutputRecorder Open(string path, string header, SecretMasker masker, TimeProvider timeProvider, long maxFileBytes = MaxFileBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(masker);
        ArgumentNullException.ThrowIfNull(timeProvider);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var recorder = new ReviewerOutputRecorder(path, writer, masker, timeProvider, maxFileBytes);
        recorder.WriteNote(header);
        return recorder;
    }

    // 保持期間と合計サイズの上限を超えたファイルを削除する。個別の削除失敗は残りの削除を止めない.
    public static (int Deleted, int Failed) Prune(string root, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Directory.Exists(root))
        {
            return (0, 0);
        }

        List<ReviewerOutputFile> files = Directory.EnumerateFiles(root, "*.log", SearchOption.AllDirectories)
            .Select(static path => new FileInfo(path))
            .Select(static info => new ReviewerOutputFile(info.FullName, info.LastWriteTimeUtc, info.Length))
            .ToList();

        int deleted = 0;
        int failed = 0;
        foreach (string path in ReviewerOutputLayout.SelectDeletions(files, now, Retention, MaxTotalBytes))
        {
            try
            {
                File.Delete(path);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }

        return (deleted, failed);
    }

    public void WriteStdout(string line) => Write("stdout", line);

    public void WriteStderr(string line) => Write("stderr", line);

    // 起動情報や終了状態など、プロセスの出力ではない注記.
    public void WriteNote(string text) => Write("note", text);

    public void Dispose()
    {
        lock (_gate)
        {
            _stopped = true;
            _writer.Dispose();
        }
    }

    private void Write(string stream, string text)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            string timestamp = _timeProvider.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            string line = $"{timestamp} [{stream}] {_masker.Mask(text)}";
            long size = Encoding.UTF8.GetByteCount(line) + 1;
            try
            {
                if (_written + size > _maxFileBytes)
                {
                    _stopped = true;
                    _writer.WriteLine($"{timestamp} [note] 1 ファイルの上限（{_maxFileBytes} バイト）に達したため、以降は記録しません。");
                    return;
                }

                _written += size;
                _writer.WriteLine(line);
            }
            catch (IOException ex)
            {
                _stopped = true;
                FailureMessage = ex.Message;
            }
        }
    }
}
