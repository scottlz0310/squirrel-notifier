// <copyright file="SettingsInputCoordinatorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class SettingsInputCoordinatorTests : IDisposable
{
    private readonly string _settingsDirectory = Path.Combine(
        Path.GetTempPath(),
        $"SettingsInputCoordinatorTests_{Guid.NewGuid()}");
    private readonly SettingsService _settingsService;
    private readonly SettingsCoordinator _settingsCoordinator;

    public SettingsInputCoordinatorTests()
    {
        _settingsService = new SettingsService(_settingsDirectory, pnpmBinDir: string.Empty);
        _settingsCoordinator = new SettingsCoordinator(_settingsService);
    }

    [Fact]
    public void SaveIfReady_ShouldIgnoreInputBeforeInitializationCompletes()
    {
        SettingsInputCoordinator coordinator = new(_settingsService, _settingsCoordinator);
        string originalGatewayUrl = _settingsService.Settings.GatewayUrl;

        SettingsSaveResult? result = coordinator.SaveIfReady(CreateInput(), isLauncherPresetApplying: false);

        result.Should().BeNull();
        _settingsService.Settings.GatewayUrl.Should().Be(originalGatewayUrl);
    }

    [Fact]
    public void SaveIfReady_ShouldIgnoreInputWhileLauncherPresetIsApplying()
    {
        SettingsInputCoordinator coordinator = new(_settingsService, _settingsCoordinator);
        coordinator.CompleteInitialization();

        SettingsSaveResult? result = coordinator.SaveIfReady(CreateInput(), isLauncherPresetApplying: true);

        result.Should().BeNull();
    }

    [Fact]
    public void SaveIfReady_ShouldDelegateAfterInitializationCompletes()
    {
        SettingsInputCoordinator coordinator = new(_settingsService, _settingsCoordinator);
        coordinator.CompleteInitialization();

        SettingsSaveResult? result = coordinator.SaveIfReady(CreateInput(), isLauncherPresetApplying: false);

        result.Should().NotBeNull();
        result!.IsSaved.Should().BeTrue();
    }

    [Fact]
    public void ToggleUpdates_ShouldIgnoreInitializationAndPersistAfterwards()
    {
        SettingsInputCoordinator coordinator = new(_settingsService, _settingsCoordinator);

        coordinator.UpdateLiveLogAutoCloseEnabled(false);
        coordinator.UpdateAutoReviewStartEnabled(true);

        _settingsService.Settings.LiveLogAutoCloseEnabled.Should().BeTrue();
        _settingsService.Settings.AutoReviewStartEnabled.Should().BeFalse();

        coordinator.CompleteInitialization();
        coordinator.UpdateLiveLogAutoCloseEnabled(false);
        coordinator.UpdateAutoReviewStartEnabled(true);

        _settingsService.Settings.LiveLogAutoCloseEnabled.Should().BeFalse();
        _settingsService.Settings.AutoReviewStartEnabled.Should().BeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_settingsDirectory))
        {
            Directory.Delete(_settingsDirectory, recursive: true);
        }
    }

    private static SettingsInput CreateInput()
    {
        LauncherAgentDefinition claude = LauncherAgentCatalog.Find("claude")!;
        return new SettingsInput(
            "mcp-resource-subscriber",
            "--skip-resource-list-check",
            "http://localhost:3000/mcp",
            "queue://review/queue",
            30000,
            claude.Command,
            claude.ReviewerArgumentsTemplate,
            claude.Command,
            claude.ReviewedArgumentsTemplate,
            300000,
            string.Empty);
    }
}
