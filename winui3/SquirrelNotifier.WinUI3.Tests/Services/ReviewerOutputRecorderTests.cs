// <copyright file="ReviewerOutputRecorderTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.IO;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class ReviewerOutputRecorderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ReviewerOutputRecorderTests_{Guid.NewGuid()}");
    private readonly SteppingTimeProvider _timeProvider = new(new DateTimeOffset(2026, 11, 5, 3, 4, 5, 6, TimeSpan.Zero));

    public ReviewerOutputRecorderTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    public void Open_ShouldWriteHeaderAndTimestampedStreamLines()
    {
        string path = Path.Combine(_tempDir, "a", "run.log");

        using (ReviewerOutputRecorder recorder = ReviewerOutputRecorder.Open(path, "header text", new SecretMasker([]), _timeProvider))
        {
            _timeProvider.Advance(TimeSpan.FromSeconds(1));
            recorder.WriteStdout("out line");
            recorder.WriteStderr("err line");
            recorder.WriteNote("note line");
            recorder.FailureMessage.Should().BeNull();
        }

        File.ReadAllText(path).Should().Be(
            "2026-11-05T03:04:05.006Z [note] header text\n"
            + "2026-11-05T03:04:06.006Z [stdout] out line\n"
            + "2026-11-05T03:04:06.006Z [stderr] err line\n"
            + "2026-11-05T03:04:06.006Z [note] note line\n");
    }

    [Fact]
    public void Write_ShouldMaskSecretsInEveryStream()
    {
        string path = Path.Combine(_tempDir, "masked.log");

        using (ReviewerOutputRecorder recorder = ReviewerOutputRecorder.Open(path, "h", new SecretMasker(["secret-value"]), _timeProvider))
        {
            recorder.WriteStdout("out secret-value");
            recorder.WriteStderr("err secret-value");
            recorder.WriteNote("note secret-value");
        }

        string text = File.ReadAllText(path);
        text.Should().NotContain("secret-value");
        text.Should().Contain("[stdout] out ***").And.Contain("[stderr] err ***").And.Contain("[note] note ***");
    }

    [Fact]
    public void Write_ShouldStopAtFileLimitAndLeaveSingleMarker()
    {
        string path = Path.Combine(_tempDir, "limited.log");

        using (ReviewerOutputRecorder recorder = ReviewerOutputRecorder.Open(path, "h", new SecretMasker([]), _timeProvider, maxFileBytes: 100))
        {
            for (int i = 0; i < 10; i++)
            {
                recorder.WriteStdout("0123456789");
            }
        }

        string[] lines = File.ReadAllLines(path);
        lines[^1].Should().Contain("1 ファイルの上限（100 バイト）に達したため、以降は記録しません。");
        lines.Should().ContainSingle(static l => l.Contains("上限", StringComparison.Ordinal));
        new FileInfo(path).Length.Should().BeLessThan(300);
    }

    [Fact]
    public void Write_ShouldBeIgnoredAfterDispose()
    {
        string path = Path.Combine(_tempDir, "disposed.log");
        ReviewerOutputRecorder recorder = ReviewerOutputRecorder.Open(path, "h", new SecretMasker([]), _timeProvider);
        recorder.Dispose();

        Action act = () => recorder.WriteStdout("late");

        act.Should().NotThrow();
        File.ReadAllText(path).Should().NotContain("late");
    }

    [Fact]
    public void Open_ShouldNotOverwriteExistingFile()
    {
        string path = Path.Combine(_tempDir, "exists.log");
        File.WriteAllText(path, "keep");

        Action act = () => ReviewerOutputRecorder.Open(path, "h", new SecretMasker([]), _timeProvider);

        act.Should().Throw<IOException>();
        File.ReadAllText(path).Should().Be("keep");
    }

    [Fact]
    public void Prune_ShouldDeleteOnlyExpiredLogFiles()
    {
        string expired = CreateLog("o/r/1/old.log", _timeProvider.GetUtcNow() - ReviewerOutputRecorder.Retention - TimeSpan.FromMinutes(1));
        string fresh = CreateLog("o/r/1/new.log", _timeProvider.GetUtcNow() - TimeSpan.FromHours(1));
        string other = CreateLog("o/r/1/memo.txt", _timeProvider.GetUtcNow() - TimeSpan.FromDays(365));

        (int deleted, int failed) = ReviewerOutputRecorder.Prune(_tempDir, _timeProvider.GetUtcNow());

        deleted.Should().Be(1);
        failed.Should().Be(0);
        File.Exists(expired).Should().BeFalse();
        File.Exists(fresh).Should().BeTrue();
        File.Exists(other).Should().BeTrue();
    }

    [Fact]
    public void Prune_ShouldReturnZeroWhenRootIsMissing()
    {
        ReviewerOutputRecorder.Prune(Path.Combine(_tempDir, "missing"), _timeProvider.GetUtcNow()).Should().Be((0, 0));
    }

    [Fact]
    public void Prune_ShouldCountFailureAndContinueWhenFileIsLocked()
    {
        string locked = CreateLog("o/r/1/locked.log", _timeProvider.GetUtcNow() - ReviewerOutputRecorder.Retention - TimeSpan.FromDays(2));
        string expired = CreateLog("o/r/1/old.log", _timeProvider.GetUtcNow() - ReviewerOutputRecorder.Retention - TimeSpan.FromDays(1));
        using var handle = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        (int deleted, int failed) = ReviewerOutputRecorder.Prune(_tempDir, _timeProvider.GetUtcNow());

        deleted.Should().Be(1);
        failed.Should().Be(1);
        File.Exists(expired).Should().BeFalse();
    }

    private string CreateLog(string relativePath, DateTimeOffset lastWrite)
    {
        string path = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
        return path;
    }

    private sealed class SteppingTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan delta) => _now += delta;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
