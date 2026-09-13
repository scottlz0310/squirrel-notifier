// <copyright file="SettingsInputParserTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public sealed class SettingsInputParserTests
{
    [Fact]
    public void ParseResourceUris_ShouldTrimRemoveEmptyAndDuplicateValues()
    {
        List<string> result = SettingsInputParser.ParseResourceUris(
            " queue://one\r\n\r\nqueue://one\n queue://two ");

        result.Should().Equal("queue://one", "queue://two");
    }

    [Fact]
    public void MergeResourceUris_ShouldKeepExistingOrderAndAppendOnlyNewValues()
    {
        string result = SettingsInputParser.MergeResourceUris(
            "queue://one\nqueue://two",
            ["queue://two", "queue://three", " "]);

        result.Should().Be("queue://one\nqueue://two\nqueue://three");
    }

    [Fact]
    public void TryParse_ShouldNormalizeNumberBoxNaNAndResolvePresets()
    {
        SettingsUpdateValues? result = SettingsInputParser.TryParse(CreateInput(
            notificationTimeoutValue: double.NaN,
            launcherTimeoutValue: double.NaN));

        result.Should().NotBeNull();
        result!.NotificationTimeoutMs.Should().Be(60000);
        result.LauncherTimeoutMs.Should().Be(1800000);
        result.ReviewerPresetId.Should().Be("claude");
        result.ReviewedPresetId.Should().Be("claude");
        result.SessionResumeEnabled.Should().BeFalse();
        result.ReviewerLauncherResumeArguments.Should().Contain("--resume {sessionId}");
    }

    [Fact]
    public void TryParse_ShouldTreatEditedResumeTemplateAsCustom()
    {
        SettingsInput input = CreateInput() with
        {
            ReviewerLauncherResumeArguments = "--resume {sessionId} --custom",
            SessionResumeEnabled = true,
        };

        SettingsUpdateValues? result = SettingsInputParser.TryParse(input);

        result.Should().NotBeNull();
        result!.ReviewerPresetId.Should().Be(LauncherAgentCatalog.CustomPresetId);
        result.SessionResumeEnabled.Should().BeTrue();
        result.ReviewerLauncherResumeArguments.Should().Be("--resume {sessionId} --custom");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n ")]
    public void TryParse_ShouldReturnNull_WhenResourceUrisAreNotReady(string resourceUris)
    {
        SettingsInputParser.TryParse(CreateInput(resourceUrisText: resourceUris)).Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(300001)]
    [InlineData(double.PositiveInfinity)]
    public void TryParse_ShouldReturnNull_WhenNotificationTimeoutIsInvalid(double timeout)
    {
        SettingsInputParser.TryParse(CreateInput(notificationTimeoutValue: timeout)).Should().BeNull();
    }

    [Fact]
    public void TryParse_ShouldReturnNull_WhenRepositoryMappingIsIncomplete()
    {
        SettingsInputParser.TryParse(CreateInput(repositoryMappings: "owner/repo=")).Should().BeNull();
    }

    private static SettingsInput CreateInput(
        string resourceUrisText = "queue://review/queue",
        double notificationTimeoutValue = 30000,
        double launcherTimeoutValue = 300000,
        string repositoryMappings = "")
    {
        LauncherAgentDefinition claude = LauncherAgentCatalog.Find("claude")!;
        return new SettingsInput(
            "mcp-resource-subscriber",
            "--skip-resource-list-check",
            "http://localhost:3000/mcp",
            resourceUrisText,
            notificationTimeoutValue,
            claude.Command,
            claude.ReviewerArgumentsTemplate,
            claude.ReviewerResumeArgumentsTemplate,
            claude.Command,
            claude.ReviewedArgumentsTemplate,
            claude.ReviewedResumeArgumentsTemplate,
            launcherTimeoutValue,
            false,
            repositoryMappings);
    }
}
