using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class SettingsServiceTests : IDisposable
{
    // #353〜#395 の codex 既定値（skill を / で呼んでいた）
    private const string _legacyCodexSlashReviewerArguments = "exec --skip-git-repo-check --json \"/thread-owl-pr-reviewer {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\"";
    private const string _legacyCodexSlashReviewedArguments = "exec --json \"/review-raven-thread-owl-cycle {owner}/{repo}#{prNumber} のレビュー指摘に対応してください\"";
    private const string _legacyCodexSlashReviewerResumeArguments = "exec resume --skip-git-repo-check --json {sessionId} \"/thread-owl-pr-reviewer {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\"";
    private const string _legacyCodexSlashReviewedResumeArguments = "exec resume --json {sessionId} \"/review-raven-thread-owl-cycle {owner}/{repo}#{prNumber} のレビュー指摘に対応してください\"";

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
        settings.SessionResumeEnabled.Should().BeFalse();
        settings.ReviewerLauncherResumeArguments.Should().Be(
            LauncherAgentCatalog.Find("claude")!.ReviewerResumeArgumentsTemplate);
        settings.ReviewedLauncherResumeArguments.Should().Be(
            LauncherAgentCatalog.Find("claude")!.ReviewedResumeArgumentsTemplate);
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
        string reviewerResumeArgs = "",
        string reviewedCmd = "reviewed-cmd",
        string reviewedArgs = "--reviewed-arg",
        string reviewedResumeArgs = "",
        int launcherTimeout = 150000,
        bool sessionResumeEnabled = false,
        string reviewerPresetId = "custom",
        string reviewedPresetId = "custom")
    {
        _settingsService.UpdateSettings(
            cmd,
            args,
            url,
            uris ?? _defaultUris,
            timeout,
            reviewerCmd,
            reviewerArgs,
            reviewerResumeArgs,
            reviewedCmd,
            reviewedArgs,
            reviewedResumeArgs,
            launcherTimeout,
            sessionResumeEnabled,
            reviewerPresetId,
            reviewedPresetId);
    }

    [Theory]
    // プリセットが解決できる場合はそのまま
    [InlineData("claude", "claude", "claude-code")]
    [InlineData("codex", "codex", "codex")]
    [InlineData("agy", "agy", "agy")]
    // レートリミット取得手段の無いプリセットは対象外のまま
    [InlineData("copilot", "copilot", null)]
    // arguments 編集で custom になっても command が一致すれば引き継ぐ（#233）
    [InlineData("custom", "claude", "claude-code")]
    [InlineData("custom", "agy", "agy")]
    [InlineData("custom", "copilot", null)]
    // command 自体がプリセットと異なる場合は対象外
    [InlineData("custom", @"C:\tools\claude.cmd", null)]
    [InlineData("custom", "my-own-agent", null)]
    public void ResolveLauncherRateLimitAgentId_ShouldFallBackToCommandMatch(
        string presetId, string command, string? expectedAgentId)
    {
        UpdateSettingsDefault(
            reviewerCmd: command,
            reviewerArgs: "--whatever",
            reviewedCmd: command,
            reviewedArgs: "--whatever",
            reviewerPresetId: presetId,
            reviewedPresetId: presetId);

        _settingsService.ResolveLauncherRateLimitAgentId(LauncherRole.Reviewer).Should().Be(expectedAgentId);
        _settingsService.ResolveLauncherRateLimitAgentId(LauncherRole.Reviewed).Should().Be(expectedAgentId);
    }

    [Fact]
    public void ResolveLauncherRateLimitAgentId_ShouldResolveEachSlotIndependently()
    {
        UpdateSettingsDefault(
            reviewerCmd: "claude",
            reviewerArgs: "--model opus",
            reviewedCmd: "my-own-agent",
            reviewedArgs: "--whatever",
            reviewerPresetId: "custom",
            reviewedPresetId: "custom");

        _settingsService.ResolveLauncherRateLimitAgentId(LauncherRole.Reviewer).Should().Be("claude-code");
        _settingsService.ResolveLauncherRateLimitAgentId(LauncherRole.Reviewed).Should().BeNull();
    }

    [Fact]
    public void ResolveLauncherProgressEventSupport_ShouldNotFallBackToCommandMatch()
    {
        // progress event 対応度は arguments（--output-format stream-json の有無）に依存するため、
        // command 一致による救済を行ってはならない（#233）
        UpdateSettingsDefault(
            reviewerCmd: "claude",
            reviewerArgs: "-p \"レビューして\"",
            reviewerPresetId: "custom");

        _settingsService.ResolveLauncherProgressEventSupport(LauncherRole.Reviewer)
            .Should().Be(ProgressEventSupport.None);
    }

    [Fact]
    public void UpdateSettings_ShouldUpdateSettings()
    {
        // Act
        UpdateSettingsDefault(
            uris: new[] { "queue://custom", "queue://custom2" },
            reviewerResumeArgs: "--reviewer-resume {sessionId}",
            reviewedResumeArgs: "--reviewed-resume {sessionId}",
            sessionResumeEnabled: true);

        // Assert
        _settingsService.Settings.SubscriberCommandPath.Should().Be("custom-cmd");
        _settingsService.Settings.SubscriberArguments.Should().Be("--arg");
        _settingsService.Settings.GatewayUrl.Should().Be("https://example.com/gw");
        _settingsService.Settings.ResourceUri.Should().Be("queue://custom");
        _settingsService.Settings.ResourceUris.Should().BeEquivalentTo(new[] { "queue://custom", "queue://custom2" });
        _settingsService.Settings.NotificationTimeoutMs.Should().Be(120000);
        _settingsService.Settings.ReviewerLauncherCommandPath.Should().Be("reviewer-cmd");
        _settingsService.Settings.ReviewerLauncherArguments.Should().Be("--reviewer-arg");
        _settingsService.Settings.ReviewerLauncherResumeArguments.Should().Be("--reviewer-resume {sessionId}");
        _settingsService.Settings.ReviewedLauncherCommandPath.Should().Be("reviewed-cmd");
        _settingsService.Settings.ReviewedLauncherArguments.Should().Be("--reviewed-arg");
        _settingsService.Settings.ReviewedLauncherResumeArguments.Should().Be("--reviewed-resume {sessionId}");
        _settingsService.Settings.LauncherTimeoutMs.Should().Be(150000);
        _settingsService.Settings.SessionResumeEnabled.Should().BeTrue();
        _settingsService.Settings.ReviewerLauncherPresetId.Should().Be("custom");
        _settingsService.Settings.ReviewedLauncherPresetId.Should().Be("custom");
    }

    [Fact]
    public void UpdateSettings_ShouldPersistSessionResumeSettings()
    {
        UpdateSettingsDefault(
            reviewerResumeArgs: "--reviewer-resume {sessionId}",
            reviewedResumeArgs: "--reviewed-resume {sessionId}",
            sessionResumeEnabled: true);

        var reloaded = new SettingsService(_settingsDirectory, pnpmBinDir: string.Empty);

        reloaded.Settings.SessionResumeEnabled.Should().BeTrue();
        reloaded.Settings.ReviewerLauncherResumeArguments.Should().Be("--reviewer-resume {sessionId}");
        reloaded.Settings.ReviewedLauncherResumeArguments.Should().Be("--reviewed-resume {sessionId}");
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

    [Fact]
    public void UpdateSettings_ShouldThrowForEmptyReviewerPresetId()
        => FluentActions.Invoking(() => UpdateSettingsDefault(reviewerPresetId: "")).Should().Throw<ArgumentException>();

    [Fact]
    public void UpdateSettings_ShouldThrowForEmptyReviewedPresetId()
        => FluentActions.Invoking(() => UpdateSettingsDefault(reviewedPresetId: "")).Should().Throw<ArgumentException>();

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    [InlineData(300001)]
    public void UpdateSettings_ShouldThrowArgumentOutOfRangeExceptionForInvalidNotificationTimeout(int invalidTimeout)
    {
        FluentActions.Invoking(() => UpdateSettingsDefault(timeout: invalidTimeout)).Should().Throw<ArgumentOutOfRangeException>();
    }

    // launcher timeout は長時間レビューサイクルを考慮し、通知タイムアウトとは別に上限 2 時間（#143）
    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    [InlineData(7200001)]
    public void UpdateSettings_ShouldThrowArgumentOutOfRangeExceptionForInvalidLauncherTimeout(int invalidTimeout)
    {
        FluentActions.Invoking(() => UpdateSettingsDefault(launcherTimeout: invalidTimeout)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void UpdateSettings_ShouldAcceptLauncherTimeoutUpToTwoHours()
    {
        UpdateSettingsDefault(launcherTimeout: 7200000);

        _settingsService.Settings.LauncherTimeoutMs.Should().Be(7200000);
    }

    [Fact]
    public void Settings_ShouldDefaultLauncherTimeoutToThirtyMinutes()
    {
        new AppSettings().LauncherTimeoutMs.Should().Be(1800000);
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

    [Fact]
    public void LauncherPresetsMigration_ShouldSetClaudePreset_WhenSettingsMatchClaudeDefaults()
    {
        // Arrange: 既定の claude command/arguments のまま LauncherPresetsMigrated が未実施の設定
        LauncherAgentDefinition claude = LauncherAgentCatalog.Find("claude")!;
        string settingsDir = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierPresetMigrationTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(settingsDir);
        string settingsPath = Path.Combine(settingsDir, "settings.json");
        var seed = new AppSettings
        {
            ReviewerLauncherCommandPath = claude.Command,
            ReviewerLauncherArguments = claude.ReviewerArgumentsTemplate,
            ReviewedLauncherCommandPath = claude.Command,
            ReviewedLauncherArguments = claude.ReviewedArgumentsTemplate,
            LauncherSlotsMigrated = true,
            LauncherPresetsMigrated = false,
        };
        File.WriteAllText(settingsPath, System.Text.Json.JsonSerializer.Serialize(seed));

        try
        {
            // Act
            var service = new SettingsService(settingsDir, pnpmBinDir: string.Empty);

            // Assert
            service.Settings.ReviewerLauncherPresetId.Should().Be("claude");
            service.Settings.ReviewedLauncherPresetId.Should().Be("claude");
            service.Settings.LauncherPresetsMigrated.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(settingsDir, true);
        }
    }

    [Fact]
    public void LauncherPresetsMigration_ShouldSetCustomPreset_WhenSettingsAreCustomized()
    {
        // Arrange: プリセットのどれとも一致しないカスタム command/arguments
        string settingsDir = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierPresetMigrationTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(settingsDir);
        string settingsPath = Path.Combine(settingsDir, "settings.json");
        var seed = new AppSettings
        {
            ReviewerLauncherCommandPath = "my-custom-tool",
            ReviewerLauncherArguments = "--custom",
            ReviewedLauncherCommandPath = "my-custom-tool",
            ReviewedLauncherArguments = "--custom",
            LauncherSlotsMigrated = true,
            LauncherPresetsMigrated = false,
        };
        File.WriteAllText(settingsPath, System.Text.Json.JsonSerializer.Serialize(seed));

        try
        {
            // Act
            var service = new SettingsService(settingsDir, pnpmBinDir: string.Empty);

            // Assert
            service.Settings.ReviewerLauncherPresetId.Should().Be(LauncherAgentCatalog.CustomPresetId);
            service.Settings.ReviewedLauncherPresetId.Should().Be(LauncherAgentCatalog.CustomPresetId);
            service.Settings.LauncherPresetsMigrated.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(settingsDir, true);
        }
    }

    [Fact]
    public void LauncherPresetsMigration_ShouldSkipWhenAlreadyMigrated()
    {
        // Arrange: 既に移行済みで、実際の command/arguments とは矛盾する presetId を持つ設定
        string settingsDir = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierPresetMigrationTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(settingsDir);
        string settingsPath = Path.Combine(settingsDir, "settings.json");
        var seed = new AppSettings
        {
            ReviewerLauncherCommandPath = "my-custom-tool",
            ReviewerLauncherArguments = "--custom",
            ReviewerLauncherPresetId = "codex",
            LauncherSlotsMigrated = true,
            LauncherPresetsMigrated = true,
        };
        File.WriteAllText(settingsPath, System.Text.Json.JsonSerializer.Serialize(seed));

        try
        {
            // Act
            var service = new SettingsService(settingsDir, pnpmBinDir: string.Empty);

            // Assert: 既に移行済みなので再判定されず、矛盾していても上書きされない
            service.Settings.ReviewerLauncherPresetId.Should().Be("codex");
        }
        finally
        {
            Directory.Delete(settingsDir, true);
        }
    }

    [Fact]
    public void ReviewedLauncherSkillMigration_ShouldRewriteLegacyDefaultArguments()
    {
        // Arrange: 旧既定値（実在しない /thread-owl-review-cycle を呼ぶテンプレート）のままの設定
        string settingsDir = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierSkillMigrationTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(settingsDir);
        var seed = new AppSettings
        {
            ReviewedLauncherCommandPath = "claude",
            ReviewedLauncherArguments = "-p \"/thread-owl-review-cycle {owner}/{repo}#{prNumber} のレビュー指摘に対応してください\"",
            LauncherSlotsMigrated = true,
            LauncherPresetsMigrated = false,
            ReviewedLauncherSkillMigrated = false,
        };
        File.WriteAllText(Path.Combine(settingsDir, "settings.json"), System.Text.Json.JsonSerializer.Serialize(seed));

        try
        {
            // Act
            var service = new SettingsService(settingsDir, pnpmBinDir: string.Empty);

            // Assert: 新既定値へ書き換わり、後続のプリセット判定でも claude と一致する
            service.Settings.ReviewedLauncherArguments.Should().Be(LauncherAgentCatalog.Find("claude")!.ReviewedArgumentsTemplate);
            service.Settings.ReviewedLauncherArguments.Should().Contain("/review-raven-thread-owl-cycle");
            service.Settings.ReviewedLauncherPresetId.Should().Be("claude");
            service.Settings.ReviewedLauncherSkillMigrated.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(settingsDir, true);
        }
    }

    [Theory]
    [InlineData("claude", "-p custom-args")]
    [InlineData("my-custom-tool", "-p \"/thread-owl-review-cycle {owner}/{repo}#{prNumber} のレビュー指摘に対応してください\"")]
    public void ReviewedLauncherSkillMigration_ShouldNotRewriteCustomizedSettings(string reviewedCmd, string reviewedArgs)
    {
        // Arrange: command / arguments のどちらかが旧既定値と異なる（カスタマイズ済み）設定
        string settingsDir = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierSkillMigrationTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(settingsDir);
        var seed = new AppSettings
        {
            ReviewedLauncherCommandPath = reviewedCmd,
            ReviewedLauncherArguments = reviewedArgs,
            LauncherSlotsMigrated = true,
            ReviewedLauncherSkillMigrated = false,
        };
        File.WriteAllText(Path.Combine(settingsDir, "settings.json"), System.Text.Json.JsonSerializer.Serialize(seed));

        try
        {
            // Act
            var service = new SettingsService(settingsDir, pnpmBinDir: string.Empty);

            // Assert: カスタマイズ済みの値は変更されない
            service.Settings.ReviewedLauncherCommandPath.Should().Be(reviewedCmd);
            service.Settings.ReviewedLauncherArguments.Should().Be(reviewedArgs);
            service.Settings.ReviewedLauncherSkillMigrated.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(settingsDir, true);
        }
    }

    [Fact]
    public void ReviewedLauncherSkillMigration_ShouldSkipWhenAlreadyMigrated()
    {
        // Arrange: migration 済みフラグが立っている場合は旧既定値でも書き換えない
        string settingsDir = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierSkillMigrationTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(settingsDir);
        const string legacyArgs = "-p \"/thread-owl-review-cycle {owner}/{repo}#{prNumber} のレビュー指摘に対応してください\"";
        var seed = new AppSettings
        {
            ReviewedLauncherCommandPath = "claude",
            ReviewedLauncherArguments = legacyArgs,
            LauncherSlotsMigrated = true,
            LauncherPresetsMigrated = true,
            ReviewedLauncherSkillMigrated = true,
        };
        File.WriteAllText(Path.Combine(settingsDir, "settings.json"), System.Text.Json.JsonSerializer.Serialize(seed));

        try
        {
            // Act
            var service = new SettingsService(settingsDir, pnpmBinDir: string.Empty);

            // Assert
            service.Settings.ReviewedLauncherArguments.Should().Be(legacyArgs);
        }
        finally
        {
            Directory.Delete(settingsDir, true);
        }
    }

    [Fact]
    public void AgyPrintTimeoutMigration_ShouldRewriteLegacyDefaultArguments()
    {
        const string legacyReviewerArgs = "-p \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\"";
        const string legacyReviewedArgs = "-p \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} のレビュー指摘に対応し、修正・返信・resolve を行ってください\"";
        string settingsDir = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierAgyTimeoutMigrationTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(settingsDir);
        var seed = new AppSettings
        {
            ReviewerLauncherCommandPath = "agy",
            ReviewerLauncherArguments = legacyReviewerArgs,
            ReviewedLauncherCommandPath = "agy",
            ReviewedLauncherArguments = legacyReviewedArgs,
            LauncherSlotsMigrated = true,
            LauncherPresetsMigrated = false,
            AgyPrintTimeoutMigrated = false,
        };
        File.WriteAllText(Path.Combine(settingsDir, "settings.json"), System.Text.Json.JsonSerializer.Serialize(seed));

        try
        {
            var service = new SettingsService(settingsDir, pnpmBinDir: string.Empty);
            LauncherAgentDefinition agy = LauncherAgentCatalog.Find("agy")!;

            service.Settings.ReviewerLauncherArguments.Should().Be(agy.ReviewerArgumentsTemplate);
            service.Settings.ReviewedLauncherArguments.Should().Be(agy.ReviewedArgumentsTemplate);
            service.Settings.ReviewerLauncherPresetId.Should().Be("agy");
            service.Settings.ReviewedLauncherPresetId.Should().Be("agy");
            service.Settings.AgyPrintTimeoutMigrated.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(settingsDir, true);
        }
    }

    [Theory]
    [InlineData("reviewer")]
    [InlineData("reviewed")]
    public void AgyPrintTimeoutMigration_ShouldNotRewriteCustomizedArguments(string roleName)
    {
        const string customArguments = "-p custom-args";
        string settingsDir = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierAgyTimeoutMigrationTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(settingsDir);
        var seed = new AppSettings
        {
            ReviewerLauncherCommandPath = "agy",
            ReviewerLauncherArguments = roleName == "reviewer" ? customArguments : "unused-reviewer-args",
            ReviewedLauncherCommandPath = "agy",
            ReviewedLauncherArguments = roleName == "reviewed" ? customArguments : "unused-reviewed-args",
            LauncherSlotsMigrated = true,
            LauncherPresetsMigrated = true,
            AgyPrintTimeoutMigrated = false,
        };
        File.WriteAllText(Path.Combine(settingsDir, "settings.json"), System.Text.Json.JsonSerializer.Serialize(seed));

        try
        {
            var service = new SettingsService(settingsDir, pnpmBinDir: string.Empty);

            string actualArguments = roleName == "reviewer"
                ? service.Settings.ReviewerLauncherArguments
                : service.Settings.ReviewedLauncherArguments;
            actualArguments.Should().Be(customArguments);
            service.Settings.AgyPrintTimeoutMigrated.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(settingsDir, true);
        }
    }

    [Fact]
    public void AgyPrintTimeoutMigration_ShouldSkipWhenAlreadyMigrated()
    {
        const string legacyArgs = "-p \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\"";
        string settingsDir = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierAgyTimeoutMigrationTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(settingsDir);
        var seed = new AppSettings
        {
            ReviewerLauncherCommandPath = "agy",
            ReviewerLauncherArguments = legacyArgs,
            LauncherSlotsMigrated = true,
            LauncherPresetsMigrated = true,
            AgyPrintTimeoutMigrated = true,
            LauncherSkillPromptMigrated = true,
        };
        File.WriteAllText(Path.Combine(settingsDir, "settings.json"), System.Text.Json.JsonSerializer.Serialize(seed));

        try
        {
            var service = new SettingsService(settingsDir, pnpmBinDir: string.Empty);

            service.Settings.ReviewerLauncherArguments.Should().Be(legacyArgs);
        }
        finally
        {
            Directory.Delete(settingsDir, true);
        }
    }

    [Fact]
    public void CodexJsonOutputMigration_ShouldRewriteLegacyDefaultArguments()
    {
        const string legacyReviewerArgs = "exec --skip-git-repo-check \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\"";
        const string legacyReviewedArgs = "exec \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} のレビュー指摘に対応し、修正・返信・resolve を行ってください\"";
        string settingsDir = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierCodexJsonMigrationTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(settingsDir);
        var seed = new AppSettings
        {
            ReviewerLauncherCommandPath = "codex",
            ReviewerLauncherArguments = legacyReviewerArgs,
            ReviewedLauncherCommandPath = "codex",
            ReviewedLauncherArguments = legacyReviewedArgs,
            LauncherSlotsMigrated = true,
            AgyPrintTimeoutMigrated = true,
            CodexReviewerWorkingDirectoryMigrated = true,
            ClaudeStreamJsonMigrated = true,
            CodexJsonOutputMigrated = false,
        };
        File.WriteAllText(Path.Combine(settingsDir, "settings.json"), System.Text.Json.JsonSerializer.Serialize(seed));

        try
        {
            var service = new SettingsService(settingsDir, pnpmBinDir: string.Empty);
            LauncherAgentDefinition codex = LauncherAgentCatalog.Find("codex")!;

            service.Settings.ReviewerLauncherArguments.Should().Be(codex.ReviewerArgumentsTemplate);
            service.Settings.ReviewedLauncherArguments.Should().Be(codex.ReviewedArgumentsTemplate);
            service.Settings.ReviewerLauncherPresetId.Should().Be("codex");
            service.Settings.ReviewedLauncherPresetId.Should().Be("codex");
            service.Settings.CodexJsonOutputMigrated.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(settingsDir, true);
        }
    }

    [Theory]
    [InlineData("reviewer")]
    [InlineData("reviewed")]
    public void CodexJsonOutputMigration_ShouldNotRewriteCustomizedArguments(string roleName)
    {
        const string customArguments = "exec custom-args";
        string settingsDir = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierCodexJsonMigrationTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(settingsDir);
        var seed = new AppSettings
        {
            ReviewerLauncherCommandPath = "codex",
            ReviewerLauncherArguments = roleName == "reviewer" ? customArguments : "unused-reviewer-args",
            ReviewedLauncherCommandPath = "codex",
            ReviewedLauncherArguments = roleName == "reviewed" ? customArguments : "unused-reviewed-args",
            LauncherSlotsMigrated = true,
            AgyPrintTimeoutMigrated = true,
            CodexReviewerWorkingDirectoryMigrated = true,
            ClaudeStreamJsonMigrated = true,
            LauncherPresetsMigrated = true,
            LauncherResumeTemplatesMigrated = true,
            CodexJsonOutputMigrated = false,
        };
        File.WriteAllText(Path.Combine(settingsDir, "settings.json"), System.Text.Json.JsonSerializer.Serialize(seed));

        try
        {
            var service = new SettingsService(settingsDir, pnpmBinDir: string.Empty);

            string actualArguments = roleName == "reviewer"
                ? service.Settings.ReviewerLauncherArguments
                : service.Settings.ReviewedLauncherArguments;
            actualArguments.Should().Be(customArguments);
            service.Settings.CodexJsonOutputMigrated.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(settingsDir, true);
        }
    }

    [Fact]
    public void CodexJsonOutputMigration_ShouldSkipWhenAlreadyMigrated()
    {
        const string legacyArgs = "exec --skip-git-repo-check \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\"";
        string settingsDir = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierCodexJsonMigrationTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(settingsDir);
        var seed = new AppSettings
        {
            ReviewerLauncherCommandPath = "codex",
            ReviewerLauncherArguments = legacyArgs,
            LauncherSlotsMigrated = true,
            AgyPrintTimeoutMigrated = true,
            CodexReviewerWorkingDirectoryMigrated = true,
            ClaudeStreamJsonMigrated = true,
            LauncherPresetsMigrated = true,
            LauncherResumeTemplatesMigrated = true,
            CodexJsonOutputMigrated = true,
            LauncherSkillPromptMigrated = true,
        };
        File.WriteAllText(Path.Combine(settingsDir, "settings.json"), System.Text.Json.JsonSerializer.Serialize(seed));

        try
        {
            var service = new SettingsService(settingsDir, pnpmBinDir: string.Empty);

            service.Settings.ReviewerLauncherArguments.Should().Be(legacyArgs);
        }
        finally
        {
            Directory.Delete(settingsDir, true);
        }
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("agy")]
    [InlineData("copilot")]
    public void LauncherSkillPromptMigration_ShouldRewriteOnlyLegacyDefaults(string presetId)
    {
        LauncherAgentDefinition definition = LauncherAgentCatalog.Find(presetId)!;
        string settingsDir = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierSkillPromptMigrationTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(settingsDir);
        var seed = new AppSettings
        {
            ReviewerLauncherCommandPath = presetId,
            ReviewerLauncherArguments = BuildLegacySkilllessArguments(presetId, reviewer: true),
            ReviewerLauncherResumeArguments = BuildLegacySkilllessResumeArguments(presetId, reviewer: true),
            ReviewedLauncherCommandPath = presetId,
            ReviewedLauncherArguments = BuildLegacySkilllessArguments(presetId, reviewer: false),
            ReviewedLauncherResumeArguments = BuildLegacySkilllessResumeArguments(presetId, reviewer: false),
            LauncherSlotsMigrated = true,
            AgyPrintTimeoutMigrated = true,
            CodexReviewerWorkingDirectoryMigrated = true,
            ClaudeStreamJsonMigrated = true,
            CodexJsonOutputMigrated = true,
            AgyStreamJsonMigrated = true,
            LauncherResumeTemplatesMigrated = true,
            LauncherPresetsMigrated = false,
        };
        File.WriteAllText(Path.Combine(settingsDir, "settings.json"), System.Text.Json.JsonSerializer.Serialize(seed));

        try
        {
            var service = new SettingsService(settingsDir, pnpmBinDir: string.Empty);

            service.Settings.ReviewerLauncherArguments.Should().Be(definition.ReviewerArgumentsTemplate);
            service.Settings.ReviewerLauncherResumeArguments.Should().Be(definition.ReviewerResumeArgumentsTemplate);
            service.Settings.ReviewedLauncherArguments.Should().Be(definition.ReviewedArgumentsTemplate);
            service.Settings.ReviewedLauncherResumeArguments.Should().Be(definition.ReviewedResumeArgumentsTemplate);
            service.Settings.ReviewerLauncherPresetId.Should().Be(presetId);
            service.Settings.ReviewedLauncherPresetId.Should().Be(presetId);
            service.Settings.LauncherSkillPromptMigrated.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(settingsDir, true);
        }
    }

    [Fact]
    public void LauncherSkillPromptMigration_ShouldPreserveCustomizedResumeArguments()
    {
        const string customResumeArguments = "exec resume --custom {sessionId}";
        string settingsDir = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierSkillPromptMigrationTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(settingsDir);
        var seed = new AppSettings
        {
            ReviewerLauncherCommandPath = "codex",
            ReviewerLauncherArguments = BuildLegacySkilllessArguments("codex", reviewer: true),
            ReviewerLauncherResumeArguments = customResumeArguments,
            LauncherSlotsMigrated = true,
            AgyPrintTimeoutMigrated = true,
            CodexReviewerWorkingDirectoryMigrated = true,
            ClaudeStreamJsonMigrated = true,
            CodexJsonOutputMigrated = true,
            AgyStreamJsonMigrated = true,
            LauncherResumeTemplatesMigrated = true,
            LauncherPresetsMigrated = true,
        };
        File.WriteAllText(Path.Combine(settingsDir, "settings.json"), System.Text.Json.JsonSerializer.Serialize(seed));

        try
        {
            var service = new SettingsService(settingsDir, pnpmBinDir: string.Empty);

            service.Settings.ReviewerLauncherArguments.Should().Be(seed.ReviewerLauncherArguments);
            service.Settings.ReviewerLauncherResumeArguments.Should().Be(customResumeArguments);
            service.Settings.LauncherSkillPromptMigrated.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(settingsDir, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CodexSkillPromptPrefixMigration_ShouldRewriteLegacySlashDefaults(bool presetsMigrated)
    {
        LauncherAgentDefinition codex = LauncherAgentCatalog.Find("codex")!;
        string settingsDir = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierCodexSkillPrefixMigrationTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(settingsDir);
        var seed = new AppSettings
        {
            ReviewerLauncherCommandPath = "codex",
            ReviewerLauncherArguments = _legacyCodexSlashReviewerArguments,
            ReviewerLauncherResumeArguments = _legacyCodexSlashReviewerResumeArguments,
            ReviewerLauncherPresetId = "codex",
            ReviewedLauncherCommandPath = "codex",
            ReviewedLauncherArguments = _legacyCodexSlashReviewedArguments,
            ReviewedLauncherResumeArguments = _legacyCodexSlashReviewedResumeArguments,
            ReviewedLauncherPresetId = "codex",
            LauncherSlotsMigrated = true,
            ReviewedLauncherSkillMigrated = true,
            AgyPrintTimeoutMigrated = true,
            CodexReviewerWorkingDirectoryMigrated = true,
            ClaudeStreamJsonMigrated = true,
            CodexJsonOutputMigrated = true,
            AgyStreamJsonMigrated = true,
            LauncherSkillPromptMigrated = true,
            LauncherResumeTemplatesMigrated = true,
            LauncherPresetsMigrated = presetsMigrated,
        };
        File.WriteAllText(Path.Combine(settingsDir, "settings.json"), System.Text.Json.JsonSerializer.Serialize(seed));

        try
        {
            var service = new SettingsService(settingsDir, pnpmBinDir: string.Empty);

            service.Settings.ReviewerLauncherArguments.Should().Be(codex.ReviewerArgumentsTemplate);
            service.Settings.ReviewerLauncherResumeArguments.Should().Be(codex.ReviewerResumeArgumentsTemplate);
            service.Settings.ReviewedLauncherArguments.Should().Be(codex.ReviewedArgumentsTemplate);
            service.Settings.ReviewedLauncherResumeArguments.Should().Be(codex.ReviewedResumeArgumentsTemplate);
            service.Settings.ReviewerLauncherArguments.Should().Contain("\"$thread-owl-pr-reviewer ");
            service.Settings.ReviewedLauncherArguments.Should().Contain("\"$review-raven-thread-owl-cycle ");
            service.Settings.ReviewerLauncherPresetId.Should().Be("codex");
            service.Settings.ReviewedLauncherPresetId.Should().Be("codex");
            service.Settings.CodexSkillPromptPrefixMigrated.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(settingsDir, true);
        }
    }

    [Theory]
    [InlineData("custom-command", _legacyCodexSlashReviewerArguments, _legacyCodexSlashReviewerResumeArguments)]
    [InlineData("codex", "exec --json \"/thread-owl-pr-reviewer custom\"", _legacyCodexSlashReviewerResumeArguments)]
    [InlineData("codex", _legacyCodexSlashReviewerArguments, "exec resume --custom {sessionId}")]
    public void CodexSkillPromptPrefixMigration_ShouldPreserveCustomizedSettings(
        string command,
        string arguments,
        string resumeArguments)
    {
        string settingsDir = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierCodexSkillPrefixMigrationTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(settingsDir);
        var seed = new AppSettings
        {
            ReviewerLauncherCommandPath = command,
            ReviewerLauncherArguments = arguments,
            ReviewerLauncherResumeArguments = resumeArguments,
            LauncherSlotsMigrated = true,
            ReviewedLauncherSkillMigrated = true,
            AgyPrintTimeoutMigrated = true,
            CodexReviewerWorkingDirectoryMigrated = true,
            ClaudeStreamJsonMigrated = true,
            CodexJsonOutputMigrated = true,
            AgyStreamJsonMigrated = true,
            LauncherSkillPromptMigrated = true,
            LauncherResumeTemplatesMigrated = true,
            LauncherPresetsMigrated = true,
        };
        File.WriteAllText(Path.Combine(settingsDir, "settings.json"), System.Text.Json.JsonSerializer.Serialize(seed));

        try
        {
            var service = new SettingsService(settingsDir, pnpmBinDir: string.Empty);

            service.Settings.ReviewerLauncherCommandPath.Should().Be(command);
            service.Settings.ReviewerLauncherArguments.Should().Be(arguments);
            service.Settings.ReviewerLauncherResumeArguments.Should().Be(resumeArguments);
            service.Settings.CodexSkillPromptPrefixMigrated.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(settingsDir, true);
        }
    }

    [Fact]
    public void AgyStreamJsonMigration_ShouldRewriteLegacyDefaultArguments()
    {
        const string legacyReviewerArgs = "--print-timeout 30m -p \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\"";
        const string legacyReviewedArgs = "--print-timeout 30m -p \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} のレビュー指摘に対応し、修正・返信・resolve を行ってください\"";
        string settingsDir = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierAgyStreamJsonMigrationTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(settingsDir);
        var seed = new AppSettings
        {
            ReviewerLauncherCommandPath = "agy",
            ReviewerLauncherArguments = legacyReviewerArgs,
            ReviewedLauncherCommandPath = "agy",
            ReviewedLauncherArguments = legacyReviewedArgs,
            LauncherSlotsMigrated = true,
            AgyPrintTimeoutMigrated = true,
            CodexReviewerWorkingDirectoryMigrated = true,
            ClaudeStreamJsonMigrated = true,
            AgyStreamJsonMigrated = false,
        };
        File.WriteAllText(Path.Combine(settingsDir, "settings.json"), System.Text.Json.JsonSerializer.Serialize(seed));

        try
        {
            var service = new SettingsService(settingsDir, pnpmBinDir: string.Empty);
            LauncherAgentDefinition agy = LauncherAgentCatalog.Find("agy")!;

            service.Settings.ReviewerLauncherArguments.Should().Be(agy.ReviewerArgumentsTemplate);
            service.Settings.ReviewedLauncherArguments.Should().Be(agy.ReviewedArgumentsTemplate);
            service.Settings.ReviewerLauncherPresetId.Should().Be("agy");
            service.Settings.ReviewedLauncherPresetId.Should().Be("agy");
            service.Settings.AgyStreamJsonMigrated.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(settingsDir, true);
        }
    }

    [Theory]
    [InlineData("reviewer")]
    [InlineData("reviewed")]
    public void AgyStreamJsonMigration_ShouldNotRewriteCustomizedArguments(string roleName)
    {
        const string customArguments = "--output-format custom";
        string settingsDir = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierAgyStreamJsonMigrationTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(settingsDir);
        var seed = new AppSettings
        {
            ReviewerLauncherCommandPath = "agy",
            ReviewerLauncherArguments = roleName == "reviewer" ? customArguments : "unused-reviewer-args",
            ReviewedLauncherCommandPath = "agy",
            ReviewedLauncherArguments = roleName == "reviewed" ? customArguments : "unused-reviewed-args",
            LauncherSlotsMigrated = true,
            AgyPrintTimeoutMigrated = true,
            CodexReviewerWorkingDirectoryMigrated = true,
            ClaudeStreamJsonMigrated = true,
            LauncherPresetsMigrated = true,
            LauncherResumeTemplatesMigrated = true,
            AgyStreamJsonMigrated = false,
        };
        File.WriteAllText(Path.Combine(settingsDir, "settings.json"), System.Text.Json.JsonSerializer.Serialize(seed));

        try
        {
            var service = new SettingsService(settingsDir, pnpmBinDir: string.Empty);

            string actualArguments = roleName == "reviewer"
                ? service.Settings.ReviewerLauncherArguments
                : service.Settings.ReviewedLauncherArguments;
            actualArguments.Should().Be(customArguments);
            service.Settings.AgyStreamJsonMigrated.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(settingsDir, true);
        }
    }

    [Fact]
    public void AgyStreamJsonMigration_ShouldSkipWhenAlreadyMigrated()
    {
        const string legacyArgs = "--print-timeout 30m -p \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\"";
        string settingsDir = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierAgyStreamJsonMigrationTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(settingsDir);
        var seed = new AppSettings
        {
            ReviewerLauncherCommandPath = "agy",
            ReviewerLauncherArguments = legacyArgs,
            LauncherSlotsMigrated = true,
            AgyPrintTimeoutMigrated = true,
            CodexReviewerWorkingDirectoryMigrated = true,
            ClaudeStreamJsonMigrated = true,
            LauncherPresetsMigrated = true,
            LauncherResumeTemplatesMigrated = true,
            AgyStreamJsonMigrated = true,
        };
        File.WriteAllText(Path.Combine(settingsDir, "settings.json"), System.Text.Json.JsonSerializer.Serialize(seed));

        try
        {
            var service = new SettingsService(settingsDir, pnpmBinDir: string.Empty);

            service.Settings.ReviewerLauncherArguments.Should().Be(legacyArgs);
        }
        finally
        {
            Directory.Delete(settingsDir, true);
        }
    }

    [Fact]
    public void CodexReviewerWorkingDirectoryMigration_ShouldRewriteLegacyDefaultArguments()
    {
        const string legacyArgs = "exec \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\"";
        string settingsDir = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierCodexWorkingDirectoryMigrationTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(settingsDir);
        var seed = new AppSettings
        {
            ReviewerLauncherCommandPath = "codex",
            ReviewerLauncherArguments = legacyArgs,
            LauncherSlotsMigrated = true,
            LauncherPresetsMigrated = false,
            CodexReviewerWorkingDirectoryMigrated = false,
        };
        File.WriteAllText(Path.Combine(settingsDir, "settings.json"), System.Text.Json.JsonSerializer.Serialize(seed));

        try
        {
            var service = new SettingsService(settingsDir, pnpmBinDir: string.Empty);

            service.Settings.ReviewerLauncherArguments.Should().Be(
                LauncherAgentCatalog.Find("codex")!.ReviewerArgumentsTemplate);
            service.Settings.ReviewerLauncherPresetId.Should().Be("codex");
            service.Settings.CodexReviewerWorkingDirectoryMigrated.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(settingsDir, true);
        }
    }

    [Theory]
    [InlineData("custom-command", "exec \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\"")]
    [InlineData("codex", "exec custom-arguments")]
    public void CodexReviewerWorkingDirectoryMigration_ShouldNotRewriteCustomizedSettings(string command, string arguments)
    {
        string settingsDir = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierCodexWorkingDirectoryMigrationTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(settingsDir);
        var seed = new AppSettings
        {
            ReviewerLauncherCommandPath = command,
            ReviewerLauncherArguments = arguments,
            LauncherSlotsMigrated = true,
            LauncherPresetsMigrated = true,
            CodexReviewerWorkingDirectoryMigrated = false,
        };
        File.WriteAllText(Path.Combine(settingsDir, "settings.json"), System.Text.Json.JsonSerializer.Serialize(seed));

        try
        {
            var service = new SettingsService(settingsDir, pnpmBinDir: string.Empty);

            service.Settings.ReviewerLauncherCommandPath.Should().Be(command);
            service.Settings.ReviewerLauncherArguments.Should().Be(arguments);
            service.Settings.CodexReviewerWorkingDirectoryMigrated.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(settingsDir, true);
        }
    }

    [Fact]
    public void ClaudeStreamJsonMigration_ShouldRewriteLegacyDefaultArguments()
    {
        const string legacyReviewerArgs = "-p \"/thread-owl-pr-reviewer {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\"";
        const string legacyReviewedArgs = "-p \"/review-raven-thread-owl-cycle {owner}/{repo}#{prNumber} のレビュー指摘に対応してください\"";
        string settingsDir = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierClaudeStreamJsonMigrationTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(settingsDir);
        var seed = new AppSettings
        {
            ReviewerLauncherCommandPath = "claude",
            ReviewerLauncherArguments = legacyReviewerArgs,
            ReviewedLauncherCommandPath = "claude",
            ReviewedLauncherArguments = legacyReviewedArgs,
            LauncherSlotsMigrated = true,
            ReviewedLauncherSkillMigrated = true,
            LauncherPresetsMigrated = false,
            ClaudeStreamJsonMigrated = false,
        };
        File.WriteAllText(Path.Combine(settingsDir, "settings.json"), System.Text.Json.JsonSerializer.Serialize(seed));

        try
        {
            var service = new SettingsService(settingsDir, pnpmBinDir: string.Empty);
            LauncherAgentDefinition claude = LauncherAgentCatalog.Find("claude")!;

            service.Settings.ReviewerLauncherArguments.Should().Be(claude.ReviewerArgumentsTemplate);
            service.Settings.ReviewedLauncherArguments.Should().Be(claude.ReviewedArgumentsTemplate);
            service.Settings.ReviewerLauncherPresetId.Should().Be("claude");
            service.Settings.ReviewedLauncherPresetId.Should().Be("claude");
            service.Settings.ClaudeStreamJsonMigrated.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(settingsDir, true);
        }
    }

    [Theory]
    [InlineData("custom-command", "-p \"/thread-owl-pr-reviewer {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\"")]
    [InlineData("claude", "-p custom-arguments")]
    public void ClaudeStreamJsonMigration_ShouldNotRewriteCustomizedSettings(string command, string arguments)
    {
        string settingsDir = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierClaudeStreamJsonMigrationTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(settingsDir);
        var seed = new AppSettings
        {
            ReviewerLauncherCommandPath = command,
            ReviewerLauncherArguments = arguments,
            ReviewedLauncherCommandPath = command,
            ReviewedLauncherArguments = arguments,
            LauncherSlotsMigrated = true,
            ReviewedLauncherSkillMigrated = true,
            LauncherPresetsMigrated = true,
            ClaudeStreamJsonMigrated = false,
        };
        File.WriteAllText(Path.Combine(settingsDir, "settings.json"), System.Text.Json.JsonSerializer.Serialize(seed));

        try
        {
            var service = new SettingsService(settingsDir, pnpmBinDir: string.Empty);

            service.Settings.ReviewerLauncherArguments.Should().Be(arguments);
            service.Settings.ReviewedLauncherArguments.Should().Be(arguments);
            service.Settings.ClaudeStreamJsonMigrated.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(settingsDir, true);
        }
    }

    [Fact]
    public void ClaudeStreamJsonMigration_ShouldSkipWhenAlreadyMigrated()
    {
        const string legacyReviewerArgs = "-p \"/thread-owl-pr-reviewer {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\"";
        string settingsDir = Path.Combine(Path.GetTempPath(), $"SquirrelNotifierClaudeStreamJsonMigrationTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(settingsDir);
        var seed = new AppSettings
        {
            ReviewerLauncherCommandPath = "claude",
            ReviewerLauncherArguments = legacyReviewerArgs,
            LauncherSlotsMigrated = true,
            ReviewedLauncherSkillMigrated = true,
            LauncherPresetsMigrated = true,
            ClaudeStreamJsonMigrated = true,
        };
        File.WriteAllText(Path.Combine(settingsDir, "settings.json"), System.Text.Json.JsonSerializer.Serialize(seed));

        try
        {
            var service = new SettingsService(settingsDir, pnpmBinDir: string.Empty);

            service.Settings.ReviewerLauncherArguments.Should().Be(legacyReviewerArgs);
        }
        finally
        {
            Directory.Delete(settingsDir, true);
        }
    }

    [Fact]
    public void UpdateRepositoryCheckoutMappings_ShouldPersistAndResolveIgnoringRepositoryCase()
    {
        string checkout = Path.Combine(_settingsDirectory, "checkout");
        _settingsService.UpdateRepositoryCheckoutMappings(new Dictionary<string, string>
        {
            ["Owner/Repo"] = checkout,
        });

        var reloaded = new SettingsService(_settingsDirectory, pnpmBinDir: string.Empty);

        reloaded.ResolveRepositoryCheckoutPath("owner/repo").Should().Be(Path.GetFullPath(checkout));
    }

    [Fact]
    public void UpdateRepositoryCheckoutMappings_ShouldRejectRelativePath()
    {
        Action act = () => _settingsService.UpdateRepositoryCheckoutMappings(new Dictionary<string, string>
        {
            ["owner/repo"] = "relative-path",
        });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Settings_ShouldDefaultReviewedLauncherArgumentsToExistingSkillName()
    {
        new AppSettings().ReviewedLauncherArguments.Should().Contain("/review-raven-thread-owl-cycle");
    }

    [Fact]
    public void Settings_ShouldDefaultLiveLogAutoCloseToEnabled()
    {
        new AppSettings().LiveLogAutoCloseEnabled.Should().BeTrue();
    }

    [Fact]
    public void UpdateLiveLogAutoCloseEnabled_ShouldPersistValue()
    {
        // Act
        _settingsService.UpdateLiveLogAutoCloseEnabled(false);

        // Assert
        _settingsService.Settings.LiveLogAutoCloseEnabled.Should().BeFalse();

        var reloaded = new SettingsService(_settingsDirectory, pnpmBinDir: string.Empty);
        reloaded.Settings.LiveLogAutoCloseEnabled.Should().BeFalse();
    }

    [Fact]
    public void Settings_ShouldDefaultAutoReviewStartToDisabled()
    {
        new AppSettings().AutoReviewStartEnabled.Should().BeFalse();
    }

    [Fact]
    public void UpdateAutoReviewStartEnabled_ShouldPersistValue()
    {
        // Act
        _settingsService.UpdateAutoReviewStartEnabled(true);

        // Assert
        _settingsService.Settings.AutoReviewStartEnabled.Should().BeTrue();

        var reloaded = new SettingsService(_settingsDirectory, pnpmBinDir: string.Empty);
        reloaded.Settings.AutoReviewStartEnabled.Should().BeTrue();
    }

    [Fact]
    public void Settings_ShouldDefaultReviewedActionToHidden()
    {
        // 押すと reviewed 側をコールドスタートする劣った導線のため、既定は非表示（#256）
        new AppSettings().ReviewedActionVisible.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UpdateReviewedActionVisible_ShouldPersistValue(bool visible)
    {
        _settingsService.UpdateReviewedActionVisible(visible);

        _settingsService.Settings.ReviewedActionVisible.Should().Be(visible);

        var reloaded = new SettingsService(_settingsDirectory, pnpmBinDir: string.Empty);
        reloaded.Settings.ReviewedActionVisible.Should().Be(visible);
    }

    [Fact]
    public void Settings_ShouldDefaultRateLimitFreshnessThresholdToFifteenMinutes()
    {
        new AppSettings().RateLimitFreshnessThresholdMinutes.Should().Be(15);
    }

    [Fact]
    public void UpdateRateLimitFreshnessThresholdMinutes_ShouldPersistValue()
    {
        // Act
        _settingsService.UpdateRateLimitFreshnessThresholdMinutes(30);

        // Assert
        _settingsService.Settings.RateLimitFreshnessThresholdMinutes.Should().Be(30);

        var reloaded = new SettingsService(_settingsDirectory, pnpmBinDir: string.Empty);
        reloaded.Settings.RateLimitFreshnessThresholdMinutes.Should().Be(30);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void UpdateRateLimitFreshnessThresholdMinutes_ShouldThrowForNonPositiveValues(int minutes)
    {
        FluentActions.Invoking(() => _settingsService.UpdateRateLimitFreshnessThresholdMinutes(minutes))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("claude", "claude-code")]
    [InlineData("codex", "codex")]
    [InlineData("agy", "agy")]
    [InlineData("copilot", null)]
    public void ResolveLauncherRateLimitAgentId_ShouldReturnMappedId_ForKnownPresets(string presetId, string? expectedRateLimitAgentId)
    {
        LauncherAgentDefinition definition = LauncherAgentCatalog.Find(presetId)!;
        _settingsService.UpdateSettings(
            "cmd", "args", "http://localhost:3000", new[] { "queue://res" }, 30000,
            definition.Command, definition.ReviewerArgumentsTemplate, definition.ReviewerResumeArgumentsTemplate,
            definition.Command, definition.ReviewedArgumentsTemplate, definition.ReviewedResumeArgumentsTemplate,
            300000, false, presetId, presetId);

        _settingsService.ResolveLauncherRateLimitAgentId(LauncherRole.Reviewer).Should().Be(expectedRateLimitAgentId);
        _settingsService.ResolveLauncherRateLimitAgentId(LauncherRole.Reviewed).Should().Be(expectedRateLimitAgentId);
    }

    [Fact]
    public void ResolveLauncherRateLimitAgentId_ShouldReturnNull_ForCustomPreset()
    {
        UpdateSettingsDefault(reviewerPresetId: "custom", reviewedPresetId: "custom");

        _settingsService.ResolveLauncherRateLimitAgentId(LauncherRole.Reviewer).Should().BeNull();
        _settingsService.ResolveLauncherRateLimitAgentId(LauncherRole.Reviewed).Should().BeNull();
    }

    [Theory]
    [InlineData("claude", "Structured")]
    [InlineData("codex", "None")]
    [InlineData("agy", "None")]
    [InlineData("copilot", "None")]
    public void ResolveLauncherProgressEventSupport_ShouldReturnMappedSupport_ForKnownPresets(string presetId, string expectedSupportName)
    {
        ProgressEventSupport expectedSupport = Enum.Parse<ProgressEventSupport>(expectedSupportName);
        LauncherAgentDefinition definition = LauncherAgentCatalog.Find(presetId)!;
        _settingsService.UpdateSettings(
            "cmd", "args", "http://localhost:3000", new[] { "queue://res" }, 30000,
            definition.Command, definition.ReviewerArgumentsTemplate, definition.ReviewerResumeArgumentsTemplate,
            definition.Command, definition.ReviewedArgumentsTemplate, definition.ReviewedResumeArgumentsTemplate,
            300000, false, presetId, presetId);

        _settingsService.ResolveLauncherProgressEventSupport(LauncherRole.Reviewer).Should().Be(expectedSupport);
        _settingsService.ResolveLauncherProgressEventSupport(LauncherRole.Reviewed).Should().Be(expectedSupport);
    }

    [Fact]
    public void ResolveLauncherProgressEventSupport_ShouldReturnNone_ForCustomPreset()
    {
        UpdateSettingsDefault(reviewerPresetId: "custom", reviewedPresetId: "custom");

        _settingsService.ResolveLauncherProgressEventSupport(LauncherRole.Reviewer).Should().Be(ProgressEventSupport.None);
        _settingsService.ResolveLauncherProgressEventSupport(LauncherRole.Reviewed).Should().Be(ProgressEventSupport.None);
    }

    private static string BuildLegacySkilllessArguments(string presetId, bool reviewer)
    {
        string prompt = reviewer
            ? "thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください"
            : "thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} のレビュー指摘に対応し、修正・返信・resolve を行ってください";

        return presetId switch
        {
            "codex" => reviewer
                ? $"exec --skip-git-repo-check --json \"{prompt}\""
                : $"exec --json \"{prompt}\"",
            "agy" => $"--print-timeout 30m --output-format stream-json -p \"{prompt}\"",
            "copilot" => $"-p \"{prompt}\"",
            _ => throw new ArgumentOutOfRangeException(nameof(presetId)),
        };
    }

    private static string BuildLegacySkilllessResumeArguments(string presetId, bool reviewer)
    {
        string prompt = reviewer
            ? "thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください"
            : "thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} のレビュー指摘に対応し、修正・返信・resolve を行ってください";

        return presetId switch
        {
            "codex" => reviewer
                ? $"exec resume --skip-git-repo-check --json {{sessionId}} \"{prompt}\""
                : $"exec resume --json {{sessionId}} \"{prompt}\"",
            "agy" => $"--print-timeout 30m --output-format stream-json --conversation {{sessionId}} -p \"{prompt}\"",
            "copilot" => $"-p \"{prompt}\" --session-id {{sessionId}}",
            _ => throw new ArgumentOutOfRangeException(nameof(presetId)),
        };
    }
}
