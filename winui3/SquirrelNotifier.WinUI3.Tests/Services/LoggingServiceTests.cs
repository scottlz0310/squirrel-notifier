using FluentAssertions;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class LoggingServiceTests : IDisposable
{
    private readonly string _logDirectory;

    public LoggingServiceTests()
    {
        // テスト専用のログ出力先を用意する
        _logDirectory = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierLogs_{Guid.NewGuid()}");
    }

    public void Dispose()
    {
        // テスト終了時に後片付けを行う
        if (Directory.Exists(_logDirectory))
        {
            Directory.Delete(_logDirectory, true);
        }
    }

    [Fact]
    public async Task WriteAsync_ShouldAppendLogAndRaiseEvent()
    {
        // Arrange
        var service = new LoggingService(_logDirectory, maxBytes: 1024);
        string? appended = null;
        service.LogAppended += (_, message) => appended = message;

        // Act
        await service.WriteAsync("テストメッセージ");

        // Assert
        service.LogDirectory.Should().Be(_logDirectory);
        string logFile = Path.Combine(_logDirectory, "winui3.log");
        File.Exists(logFile).Should().BeTrue();
        string content = await File.ReadAllTextAsync(logFile);
        content.Should().Contain("テストメッセージ");
        appended.Should().NotBeNull();
        appended!.Should().Contain("テストメッセージ");
    }

    [Fact]
    public async Task WriteAsync_ShouldRotateWhenLimitExceeded()
    {
        // Arrange
        Directory.CreateDirectory(_logDirectory);
        string logFile = Path.Combine(_logDirectory, "winui3.log");
        await File.WriteAllTextAsync(logFile, new string('x', 32));
        var service = new LoggingService(_logDirectory, maxBytes: 16);

        // Act
        await service.WriteAsync("ローテーション確認");

        // Assert
        string[] archives = Directory.GetFiles(_logDirectory, "winui3-*.log");
        archives.Should().NotBeEmpty();
        string newContent = await File.ReadAllTextAsync(logFile);
        newContent.Should().Contain("ローテーション確認");
    }

    [Fact]
    public void Constructor_ShouldThrowWhenDirectoryIsInvalid()
    {
        // Act
        Action act = () => new LoggingService(string.Empty, 10);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_ShouldThrowWhenMaxBytesIsInvalid()
    {
        // Act
        Action act = () => new LoggingService(_logDirectory, 0);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task WriteAsync_ShouldHandleLockedFileGracefully()
    {
        // Arrange
        Directory.CreateDirectory(_logDirectory);
        string logFile = Path.Combine(_logDirectory, "winui3.log");
        await File.WriteAllTextAsync(logFile, new string('y', 32));
        using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.None);
        var service = new LoggingService(_logDirectory, maxBytes: 8);

        // Act
        await service.WriteAsync("ロック中の書き込み");

        // Assert
        File.Exists(logFile).Should().BeTrue();
    }

    [Fact]
    public void DefaultConstructor_ShouldUseLocalAppData()
    {
        // Act
        var service = new LoggingService();

        // Assert
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SquirrelNotifier", "logs");
        service.LogDirectory.Should().Be(expected);
    }
}
