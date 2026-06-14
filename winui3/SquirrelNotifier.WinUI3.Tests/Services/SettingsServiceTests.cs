using FluentAssertions;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class SettingsServiceTests : IDisposable
{
    private readonly string _settingsDirectory;
    private readonly SettingsService _settingsService;

    public SettingsServiceTests()
    {
        // テスト専用の一時ディレクトリを使用する
        _settingsDirectory = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierTests_{Guid.NewGuid()}");

        string? oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", string.Empty);
            _settingsService = new SettingsService(_settingsDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
        }
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
        settings.SubscriberCommandPath.Should().Be("mcp-resource-subscriber");
        settings.SubscriberArguments.Should().BeEmpty();
        settings.GatewayUrl.Should().Be("http://localhost:3000");
        settings.ResourceUri.Should().Be("queue://review/queue");
        settings.NotificationTimeoutMs.Should().Be(60000);
    }

    [Fact]
    public void UpdateSettings_ShouldUpdateSettings()
    {
        // Act
        _settingsService.UpdateSettings("custom-cmd", "--arg", "https://example.com/gw", "queue://custom", 120000, "launcher-cmd", "--launcher-arg", 150000);

        // Assert
        _settingsService.Settings.SubscriberCommandPath.Should().Be("custom-cmd");
        _settingsService.Settings.SubscriberArguments.Should().Be("--arg");
        _settingsService.Settings.GatewayUrl.Should().Be("https://example.com/gw");
        _settingsService.Settings.ResourceUri.Should().Be("queue://custom");
        _settingsService.Settings.NotificationTimeoutMs.Should().Be(120000);
        _settingsService.Settings.LauncherCommandPath.Should().Be("launcher-cmd");
        _settingsService.Settings.LauncherArguments.Should().Be("--launcher-arg");
        _settingsService.Settings.LauncherTimeoutMs.Should().Be(150000);
    }

    [Theory]
    [InlineData("", "--arg", "http://localhost:3000", "queue://custom", 60000, "launcher-cmd", 60000)] // Empty command path
    [InlineData("cmd", "--arg", "invalid-url", "queue://custom", 60000, "launcher-cmd", 60000)] // Invalid URL
    [InlineData("cmd", "--arg", "ftp://localhost:3000", "queue://custom", 60000, "launcher-cmd", 60000)] // Non-http/https URL
    [InlineData("cmd", "--arg", "http://localhost:3000", "", 60000, "launcher-cmd", 60000)] // Empty resource URI
    [InlineData("cmd", "--arg", "http://localhost:3000", "queue://custom", 60000, "", 60000)] // Empty launcher path
    public void UpdateSettings_ShouldThrowArgumentExceptionForInvalidInputs(
        string cmd, string args, string url, string uri, int timeout, string launcherCmd, int launcherTimeout)
    {
        // Act & Assert
        Action act = () => _settingsService.UpdateSettings(cmd, args, url, uri, timeout, launcherCmd, "--launcher-arg", launcherTimeout);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    [InlineData(300001)]
    public void UpdateSettings_ShouldThrowArgumentOutOfRangeExceptionForInvalidTimeout(int invalidTimeout)
    {
        // Act & Assert
        Action act1 = () => _settingsService.UpdateSettings("cmd", "--arg", "http://localhost:3000", "queue://custom", invalidTimeout, "launcher-cmd", "--launcher-arg", 60000);
        act1.Should().Throw<ArgumentOutOfRangeException>();

        Action act2 = () => _settingsService.UpdateSettings("cmd", "--arg", "http://localhost:3000", "queue://custom", 60000, "launcher-cmd", "--launcher-arg", invalidTimeout);
        act2.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void SettingsService_ShouldLoadExistingFile()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        string settingsPath = Path.Combine(tempDir, "settings.json");
        var settingsObj = new AppSettings
        {
            SubscriberCommandPath = "saved-cmd",
            GatewayUrl = "https://saved-url.com",
            ResourceUri = "queue://saved",
            NotificationTimeoutMs = 30000
        };
        File.WriteAllText(settingsPath, System.Text.Json.JsonSerializer.Serialize(settingsObj));

        try
        {
            // Act
            var service = new SettingsService(tempDir);

            // Assert
            service.Settings.SubscriberCommandPath.Should().Be("saved-cmd");
            service.Settings.GatewayUrl.Should().Be("https://saved-url.com");
            service.Settings.ResourceUri.Should().Be("queue://saved");
            service.Settings.NotificationTimeoutMs.Should().Be(30000);
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

    [Fact]
    public void SettingsChanged_ShouldBeRaisedWhenSettingsUpdated()
    {
        // Arrange
        bool eventRaised = false;
        _settingsService.SettingsChanged += (_, _) => eventRaised = true;

        // Act
        _settingsService.UpdateSettings("cmd", "--arg", "http://localhost:3000", "queue://custom", 60000, "launcher-cmd", "--launcher-arg", 60000);

        // Assert
        eventRaised.Should().BeTrue();
    }
}
