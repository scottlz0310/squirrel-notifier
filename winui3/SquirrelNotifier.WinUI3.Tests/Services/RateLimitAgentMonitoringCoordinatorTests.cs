// <copyright file="RateLimitAgentMonitoringCoordinatorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.ComponentModel;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class RateLimitAgentMonitoringCoordinatorTests : IDisposable
{
    private readonly string _settingsDirectory = Path.Combine(
        Path.GetTempPath(),
        $"RateLimitAgentMonitoringCoordinatorTests_{Guid.NewGuid()}");
    private readonly SettingsService _settingsService;

    public RateLimitAgentMonitoringCoordinatorTests()
    {
        string? oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
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
        if (Directory.Exists(_settingsDirectory))
        {
            Directory.Delete(_settingsDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, nameof(RateLimitAgentOption.IsMonitored))]
    [InlineData(true, nameof(RateLimitAgentOption.DisplayName))]
    public void Handle_WhenChangeIsNotApplicable_ShouldNotPersist(
        bool initializationCompleted,
        string propertyName)
    {
        RateLimitAgentMonitoringCoordinator coordinator = CreateCoordinator();
        if (initializationCompleted)
        {
            coordinator.CompleteInitialization();
        }

        int settingsChangedCount = 0;
        _settingsService.SettingsChanged += (_, _) => settingsChangedCount++;

        coordinator.Handle(
            [CreateOption("claude-code", isMonitored: true)],
            new PropertyChangedEventArgs(propertyName));

        settingsChangedCount.Should().Be(0);
    }

    [Fact]
    public void Handle_WhenMonitoredOptionChanges_ShouldPersistSelectedAgentIds()
    {
        RateLimitAgentMonitoringCoordinator coordinator = CreateCoordinator();
        coordinator.CompleteInitialization();
        RateLimitAgentOption monitored = CreateOption("claude-code", isMonitored: true);
        RateLimitAgentOption unmonitored = CreateOption("agy", isMonitored: false);
        RateLimitAgentOption alsoMonitored = CreateOption("codex", isMonitored: true);

        coordinator.Handle(
            [monitored, unmonitored, alsoMonitored],
            new PropertyChangedEventArgs(nameof(RateLimitAgentOption.IsMonitored)));

        _settingsService.Settings.RateLimitMonitoredAgentIds.Should()
            .Equal("claude-code", "codex");
        new SettingsService(_settingsDirectory, pnpmBinDir: string.Empty)
            .Settings.RateLimitMonitoredAgentIds.Should()
            .Equal("claude-code", "codex");
    }

    private RateLimitAgentMonitoringCoordinator CreateCoordinator()
        => new(_settingsService);

    private static RateLimitAgentOption CreateOption(string id, bool isMonitored)
    {
        RateLimitAgentOption option = new(id, id, isAvailable: true)
        {
            IsMonitored = isMonitored,
        };
        return option;
    }
}
