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
            // PATH を空にして PATH 検索を無効化し、pnpmBinDir: string.Empty で pnpm プローブも無効化する
            Environment.SetEnvironmentVariable("PATH", string.Empty);
            _settingsService = new SettingsService(_settingsDirectory, pnpmBinDir: string.Empty);
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
        // pnpmBinDir=null なので pnpm プローブなし → PATH も空 → デフォルト名のまま
        settings.SubscriberCommandPath.Should().Be("mcp-resource-subscriber");
        settings.SubscriberArguments.Should().BeEmpty();
        settings.GatewayUrl.Should().Be("http://localhost:3000");
        settings.ResourceUri.Should().Be("queue://review/queue");
        settings.NotificationTimeoutMs.Should().Be(60000);
    }

    [Fact]
    public void ResolveCommandPath_ShouldReturnCommandName_WhenNotFound()
    {
        string result = SettingsService.ResolveCommandPath("nonexistent-tool-xyz", pnpmBinDir: null);
        result.Should().Be("nonexistent-tool-xyz");
    }

    [Fact]
    public void ResolveCommandPath_ShouldFindExecutable_InPnpmBinDir()
    {
        // Arrange: 一時ディレクトリに偽の実行ファイルを作成
        string tempDir = Path.Combine(Path.GetTempPath(), $"pnpm_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        string fakeExe = Path.Combine(tempDir, "my-tool.cmd");
        File.WriteAllText(fakeExe, "@echo off");

        try
        {
            // pathEnv を直接渡すことで global PATH を操作せずに検証
            string result = SettingsService.ResolveCommandPath("my-tool", pnpmBinDir: tempDir, pathEnv: string.Empty);
            result.Should().Be(Path.GetFullPath(fakeExe));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ResolveCommandPath_ShouldPreferPath_OverPnpmBinDir()
    {
        // Arrange: PATH 用と pnpm 用に別々の偽ファイルを作成
        string pathDir = Path.Combine(Path.GetTempPath(), $"path_test_{Guid.NewGuid()}");
        string pnpmDir = Path.Combine(Path.GetTempPath(), $"pnpm_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(pathDir);
        Directory.CreateDirectory(pnpmDir);
        string pathExe = Path.Combine(pathDir, "my-tool.cmd");
        string pnpmExe = Path.Combine(pnpmDir, "my-tool.cmd");
        File.WriteAllText(pathExe, "@echo off");
        File.WriteAllText(pnpmExe, "@echo off");

        try
        {
            // pathEnv を直接渡すことで global PATH を操作せずに検証
            string result = SettingsService.ResolveCommandPath("my-tool", pnpmBinDir: pnpmDir, pathEnv: pathDir);
            result.Should().Be(Path.GetFullPath(pathExe));
        }
        finally
        {
            Directory.Delete(pathDir, true);
            Directory.Delete(pnpmDir, true);
        }
    }

    private static readonly IReadOnlyList<string> _defaultUris = new[] { "queue://custom" };

    private void UpdateSettingsDefault(
        string cmd = "custom-cmd",
        string args = "--arg",
        string url = "https://example.com/gw",
        IReadOnlyList<string>? uris = null,
        int timeout = 120000,
        string reviewerCmd = "reviewer-cmd",
        string reviewerArgs = "--reviewer-arg",
        string reviewedCmd = "reviewed-cmd",
        string reviewedArgs = "--reviewed-arg",
        int launcherTimeout = 150000)
    {
        _settingsService.UpdateSettings(cmd, args, url, uris ?? _defaultUris, timeout, reviewerCmd, reviewerArgs, reviewedCmd, reviewedArgs, launcherTimeout);
    }

    [Fact]
    public void UpdateSettings_ShouldUpdateSettings()
    {
        // Act
        UpdateSettingsDefault(uris: new[] { "queue://custom", "queue://custom2" });

        // Assert
        _settingsService.Settings.SubscriberCommandPath.Should().Be("custom-cmd");
        _settingsService.Settings.SubscriberArguments.Should().Be("--arg");
        _settingsService.Settings.GatewayUrl.Should().Be("https://example.com/gw");
        _settingsService.Settings.ResourceUri.Should().Be("queue://custom");
        _settingsService.Settings.ResourceUris.Should().BeEquivalentTo(new[] { "queue://custom", "queue://custom2" });
        _settingsService.Settings.NotificationTimeoutMs.Should().Be(120000);
        _settingsService.Settings.ReviewerLauncherCommandPath.Should().Be("reviewer-cmd");
        _settingsService.Settings.ReviewerLauncherArguments.Should().Be("--reviewer-arg");
        _settingsService.Settings.ReviewedLauncherCommandPath.Should().Be("reviewed-cmd");
        _settingsService.Settings.ReviewedLauncherArguments.Should().Be("--reviewed-arg");
        _settingsService.Settings.LauncherTimeoutMs.Should().Be(150000);
    }

    [Fact]
    public void UpdateSettings_ShouldThrowForEmptyCommandPath()
        => FluentActions.Invoking(() => UpdateSettingsDefault(cmd: "")).Should().Throw<ArgumentException>();

    [Fact]
    public void UpdateSettings_ShouldThrowForInvalidGatewayUrl()
        => FluentActions.Invoking(() => UpdateSettingsDefault(url: "invalid-url")).Should().Throw<ArgumentException>();

    [Fact]
    public void UpdateSettings_ShouldThrowForNonHttpGatewayUrl()
        => FluentActions.Invoking(() => UpdateSettingsDefault(url: "ftp://localhost:3000")).Should().Throw<ArgumentException>();

    [Fact]
    public void UpdateSettings_ShouldThrowForEmptyResourceUris()
        => FluentActions.Invoking(() => UpdateSettingsDefault(uris: Array.Empty<string>())).Should().Throw<ArgumentException>();

    [Fact]
    public void UpdateSettings_ShouldThrowForBlankResourceUri()
        => FluentActions.Invoking(() => UpdateSettingsDefault(uris: new[] { "  " })).Should().Throw<ArgumentException>();

    [Fact]
    public void UpdateSettings_ShouldThrowForEmptyReviewerCommandPath()
        => FluentActions.Invoking(() => UpdateSettingsDefault(reviewerCmd: "")).Should().Throw<ArgumentException>();

    [Fact]
    public void UpdateSettings_ShouldThrowForEmptyReviewedCommandPath()
        => FluentActions.Invoking(() => UpdateSettingsDefault(reviewedCmd: "")).Should().Throw<ArgumentException>();

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    [InlineData(300001)]
    public void UpdateSettings_ShouldThrowArgumentOutOfRangeExceptionForInvalidTimeout(int invalidTimeout)
    {
        FluentActions.Invoking(() => UpdateSettingsDefault(timeout: invalidTimeout)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => UpdateSettingsDefault(launcherTimeout: invalidTimeout)).Should().Throw<ArgumentOutOfRangeException>();
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
        UpdateSettingsDefault();

        // Assert
        eventRaised.Should().BeTrue();
    }

    [Fact]
    public void SettingsDirectory_ShouldReturnConstructorValue()
    {
        _settingsService.SettingsDirectory.Should().Be(_settingsDirectory);
    }

    [Fact]
    public void UpdateRateLimitMonitoredAgentIds_ShouldUpdateAndPersist()
    {
        // Arrange
        bool eventRaised = false;
        _settingsService.SettingsChanged += (_, _) => eventRaised = true;

        // Act
        _settingsService.UpdateRateLimitMonitoredAgentIds(new[] { "claude-code", "agy" });

        // Assert
        _settingsService.Settings.RateLimitMonitoredAgentIds.Should().BeEquivalentTo(new[] { "claude-code", "agy" });
        eventRaised.Should().BeTrue();

        // Verify persistence
        var anotherService = new SettingsService(_settingsDirectory, pnpmBinDir: string.Empty);
        anotherService.Settings.RateLimitMonitoredAgentIds.Should().BeEquivalentTo(new[] { "claude-code", "agy" });
    }

    [Fact]
    public void UpdateRateLimitMonitoredAgentIds_ShouldThrow_WhenNull()
        => FluentActions.Invoking(() => _settingsService.UpdateRateLimitMonitoredAgentIds(null!)).Should().Throw<ArgumentNullException>();

    [Fact]
    public void UpdateLastSkippedVersion_ShouldUpdateAndPersistVersion()
    {
        // Arrange
        bool eventRaised = false;
        _settingsService.SettingsChanged += (_, _) => eventRaised = true;

        // Act
        _settingsService.UpdateLastSkippedVersion("v3.1.0");

        // Assert
        _settingsService.Settings.LastSkippedVersion.Should().Be("v3.1.0");
        eventRaised.Should().BeTrue();

        // Verify persistence
        var anotherService = new SettingsService(_settingsDirectory);
        anotherService.Settings.LastSkippedVersion.Should().Be("v3.1.0");
    }

    [Fact]
    public void LauncherSlotsMigration_ShouldMigrateCustomArgsOnlySettings()
    {
        // Arrange: LauncherCommandPath は既定値のまま、LauncherArguments のみカスタマイズされた設定ファイルを作成
        string settingsDir = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierMigrationTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(settingsDir);
        string settingsPath = Path.Combine(settingsDir, "settings.json");
        File.WriteAllText(settingsPath, """
            {
                "LauncherCommandPath": "review-raven",
                "LauncherArguments": "custom-arg --repo {owner}/{repo}",
                "LauncherSlotsMigrated": false
            }
            """);

        try
        {
            // Act
            var service = new SettingsService(settingsDir, pnpmBinDir: string.Empty);

            // Assert: path/args を組として reviewer スロットに移行される
            service.Settings.ReviewerLauncherCommandPath.Should().Be("review-raven");
            service.Settings.ReviewerLauncherArguments.Should().Be("custom-arg --repo {owner}/{repo}");
            service.Settings.LauncherSlotsMigrated.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(settingsDir, true);
        }
    }

    [Fact]
    public void LauncherSlotsMigration_ShouldMigrateCustomPathSettings()
    {
        // Arrange: LauncherCommandPath のみカスタマイズされた設定ファイルを作成
        string settingsDir = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierMigrationTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(settingsDir);
        string settingsPath = Path.Combine(settingsDir, "settings.json");
        File.WriteAllText(settingsPath, """
            {
                "LauncherCommandPath": "my-custom-review-tool",
                "LauncherArguments": "review --interactive --repo {owner}/{repo} --pr {prNumber}",
                "LauncherSlotsMigrated": false
            }
            """);

        try
        {
            // Act
            var service = new SettingsService(settingsDir, pnpmBinDir: string.Empty);

            // Assert: path/args を組として reviewer スロットに移行される
            service.Settings.ReviewerLauncherCommandPath.Should().Be("my-custom-review-tool");
            service.Settings.ReviewerLauncherArguments.Should().Be("review --interactive --repo {owner}/{repo} --pr {prNumber}");
            service.Settings.LauncherSlotsMigrated.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(settingsDir, true);
        }
    }

    [Fact]
    public void LauncherSlotsMigration_ShouldMigrateBothCustomPathAndArgs()
    {
        // Arrange: LauncherCommandPath と LauncherArguments の両方をカスタマイズ
        string settingsDir = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierMigrationTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(settingsDir);
        string settingsPath = Path.Combine(settingsDir, "settings.json");
        File.WriteAllText(settingsPath, """
            {
                "LauncherCommandPath": "my-custom-tool",
                "LauncherArguments": "my-custom-arg --repo {owner}/{repo}",
                "LauncherSlotsMigrated": false
            }
            """);

        try
        {
            // Act
            var service = new SettingsService(settingsDir, pnpmBinDir: string.Empty);

            // Assert: path/args を組として reviewer スロットに移行される
            service.Settings.ReviewerLauncherCommandPath.Should().Be("my-custom-tool");
            service.Settings.ReviewerLauncherArguments.Should().Be("my-custom-arg --repo {owner}/{repo}");
            service.Settings.LauncherSlotsMigrated.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(settingsDir, true);
        }
    }

    [Fact]
    public void LauncherSlotsMigration_ShouldSkipWhenAlreadyMigrated()
    {
        // Arrange: 既に移行済みの設定
        string settingsDir = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierMigrationTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(settingsDir);
        string settingsPath = Path.Combine(settingsDir, "settings.json");
        File.WriteAllText(settingsPath, """
            {
                "LauncherCommandPath": "review-raven",
                "LauncherArguments": "custom-arg",
                "ReviewerLauncherCommandPath": "my-migrated-tool",
                "LauncherSlotsMigrated": true
            }
            """);

        try
        {
            // Act
            var service = new SettingsService(settingsDir, pnpmBinDir: string.Empty);

            // Assert: 移行済みなので reviewer スロットは上書きされない
            service.Settings.ReviewerLauncherCommandPath.Should().Be("my-migrated-tool");
        }
        finally
        {
            Directory.Delete(settingsDir, true);
        }
    }
}
