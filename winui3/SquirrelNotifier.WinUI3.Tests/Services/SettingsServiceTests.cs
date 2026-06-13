using System.Text.Json;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class SettingsServiceTests : IDisposable
{
    private readonly string _settingsDirectory;
    private readonly SettingsService _settingsService;

    public SettingsServiceTests()
    {
        // テスト専用の一時ディレクトリを使用する
        _settingsDirectory = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierTests_{Guid.NewGuid()}");
        _settingsService = new SettingsService(_settingsDirectory);
    }

    public void Dispose()
    {
        // テスト用に作成したディレクトリを削除する
        if (Directory.Exists(_settingsDirectory))
        {
            Directory.Delete(_settingsDirectory, true);
        }
    }

    [Fact]
    public void Settings_ShouldHaveDefaultValues()
    {
        // Arrange & Act
        AppSettings settings = _settingsService.Settings;

        // Assert
        settings.CheckIntervalHours.Should().Be(2);
    }

    [Fact]
    public void UpdateCheckInterval_ShouldUpdateSettings()
    {
        // Arrange
        const int newInterval = 5;

        // Act
        _settingsService.UpdateCheckInterval(newInterval);

        // Assert
        _settingsService.Settings.CheckIntervalHours.Should().Be(newInterval);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(25)]
    [InlineData(100)]
    public void UpdateCheckInterval_ShouldThrowForInvalidValues(int invalidInterval)
    {
        // Act & Assert
        Action act = () => _settingsService.UpdateCheckInterval(invalidInterval);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void SettingsService_ShouldLoadExistingFile()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        string settingsPath = Path.Combine(tempDir, "settings.json");
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new AppSettings { CheckIntervalHours = 7 }));

        try
        {
            // Act
            var service = new SettingsService(tempDir);

            // Assert
            service.Settings.CheckIntervalHours.Should().Be(7);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SettingsService_ShouldThrowWhenDirectoryIsInvalid()
    {
        // Act
        Action act = () => new SettingsService(string.Empty);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(24)]
    public void UpdateCheckInterval_ShouldAcceptValidValues(int validInterval)
    {
        // Act
        Action act = () => _settingsService.UpdateCheckInterval(validInterval);

        // Assert
        act.Should().NotThrow();
        _settingsService.Settings.CheckIntervalHours.Should().Be(validInterval);
    }

    [Fact]
    public void SettingsChanged_ShouldBeRaisedWhenSettingsUpdated()
    {
        // Arrange
        bool eventRaised = false;
        _settingsService.SettingsChanged += (_, _) => eventRaised = true;

        // Act
        _settingsService.UpdateCheckInterval(3);

        // Assert
        eventRaised.Should().BeTrue();
    }
}
