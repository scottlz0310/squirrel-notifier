// <copyright file="ObservationSettingsTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class ObservationSettingsTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"ObservationSettingsTests_{Guid.NewGuid():D}");

    public ObservationSettingsTests()
    {
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public void AppSettings_ShouldDisableTheObservationPublishingByDefault()
    {
        AppSettings settings = new();

        settings.ObservationPublishEnabled.Should().BeFalse("既存の利用者の動作は変えない（opt-in）");
        settings.ObservationPublishUrl.Should().BeEmpty();
        settings.ObservationProducerId.Should().Be("squirrel-notifier");
    }

    [Fact]
    public void SettingsService_ShouldKeepTheDefaults_WhenAnExistingSettingsFileHasNoObservationFields()
    {
        File.WriteAllText(Path.Combine(_testDirectory, "settings.json"), "{\"GatewayUrl\":\"http://gateway.example\"}");

        SettingsService service = new(_testDirectory, pnpmBinDir: string.Empty);

        service.Settings.GatewayUrl.Should().Be("http://gateway.example");
        service.Settings.ObservationPublishEnabled.Should().BeFalse();
        service.Settings.ObservationPublishUrl.Should().BeEmpty();
        service.Settings.ObservationProducerId.Should().Be("squirrel-notifier");
    }

    [Fact]
    public void SettingsService_ShouldReadTheObservationFieldsFromTheSettingsFile()
    {
        File.WriteAllText(
            Path.Combine(_testDirectory, "settings.json"),
            "{\"ObservationPublishEnabled\":true,\"ObservationPublishUrl\":\"https://gateway.example/mcp/review-raven-internal\",\"ObservationProducerId\":\"squirrel-pc2\"}");

        SettingsService service = new(_testDirectory, pnpmBinDir: string.Empty);

        service.Settings.ObservationPublishEnabled.Should().BeTrue();
        service.Settings.ObservationPublishUrl.Should().Be("https://gateway.example/mcp/review-raven-internal");
        service.Settings.ObservationProducerId.Should().Be("squirrel-pc2");
    }
}
