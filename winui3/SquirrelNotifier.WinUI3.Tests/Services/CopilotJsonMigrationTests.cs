// <copyright file="CopilotJsonMigrationTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text.Json;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class CopilotJsonMigrationTests : IDisposable
{
    private const string _reviewerArguments = "-p \"/thread-owl-pr-reviewer {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\"";
    private const string _reviewedArguments = "-p \"/review-raven-thread-owl-cycle {owner}/{repo}#{prNumber} のレビュー指摘に対応してください\"";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"SquirrelCopilotMigration_{Guid.NewGuid():N}");

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Migration_ShouldUpdateBothRolesAndPersistOnce(bool presetsMigrated, bool resumeMigrated)
    {
        AppSettings seed = CreateSeed();
        seed.LauncherPresetsMigrated = presetsMigrated;
        seed.LauncherResumeTemplatesMigrated = resumeMigrated;
        if (!resumeMigrated)
        {
            seed.ReviewerLauncherResumeArguments = string.Empty;
            seed.ReviewedLauncherResumeArguments = string.Empty;
        }

        Save(seed);
        var service = new SettingsService(_directory, pnpmBinDir: string.Empty);
        LauncherAgentDefinition definition = LauncherAgentCatalog.Find("copilot")!;

        service.Settings.ReviewerLauncherArguments.Should().Be(definition.ReviewerArgumentsTemplate);
        service.Settings.ReviewedLauncherArguments.Should().Be(definition.ReviewedArgumentsTemplate);
        service.Settings.ReviewerLauncherResumeArguments.Should().Be(definition.ReviewerResumeArgumentsTemplate);
        service.Settings.ReviewedLauncherResumeArguments.Should().Be(definition.ReviewedResumeArgumentsTemplate);
        service.Settings.ReviewerLauncherPresetId.Should().Be("copilot");
        service.Settings.ReviewedLauncherPresetId.Should().Be("copilot");
        service.Settings.CopilotJsonOutputMigrated.Should().BeTrue();
        new SettingsService(_directory, pnpmBinDir: string.Empty).Settings.ReviewerLauncherArguments.Should().Be(definition.ReviewerArgumentsTemplate);
    }

    [Theory]
    [InlineData("Reviewer", "command")]
    [InlineData("Reviewer", "arguments")]
    [InlineData("Reviewer", "resume")]
    [InlineData("Reviewed", "command")]
    [InlineData("Reviewed", "arguments")]
    [InlineData("Reviewed", "resume")]
    public void Migration_ShouldPreserveCustomizedSlotAndMigrateOtherRole(string roleName, string customized)
    {
        AppSettings seed = CreateSeed();
        bool reviewer = roleName == "Reviewer";
        if (reviewer)
        {
            seed.ReviewerLauncherCommandPath = customized == "command" ? "custom-copilot" : "copilot";
            seed.ReviewerLauncherArguments += customized == "arguments" ? " --model custom" : string.Empty;
            seed.ReviewerLauncherResumeArguments += customized == "resume" ? " --model custom" : string.Empty;
        }
        else
        {
            seed.ReviewedLauncherCommandPath = customized == "command" ? "custom-copilot" : "copilot";
            seed.ReviewedLauncherArguments += customized == "arguments" ? " --model custom" : string.Empty;
            seed.ReviewedLauncherResumeArguments += customized == "resume" ? " --model custom" : string.Empty;
        }

        Save(seed);
        AppSettings actual = new SettingsService(_directory, pnpmBinDir: string.Empty).Settings;
        if (reviewer)
        {
            actual.ReviewerLauncherCommandPath.Should().Be(seed.ReviewerLauncherCommandPath);
            actual.ReviewerLauncherArguments.Should().Be(seed.ReviewerLauncherArguments);
            actual.ReviewerLauncherResumeArguments.Should().Be(seed.ReviewerLauncherResumeArguments);
            actual.ReviewedLauncherArguments.Should().Contain("--output-format json");
        }
        else
        {
            actual.ReviewedLauncherCommandPath.Should().Be(seed.ReviewedLauncherCommandPath);
            actual.ReviewedLauncherArguments.Should().Be(seed.ReviewedLauncherArguments);
            actual.ReviewedLauncherResumeArguments.Should().Be(seed.ReviewedLauncherResumeArguments);
            actual.ReviewerLauncherArguments.Should().Contain("--output-format json");
        }

        actual.CopilotJsonOutputMigrated.Should().BeTrue();
    }

    [Fact]
    public void Migration_ShouldNotReapplyAfterUserRestoresTextOutput()
    {
        AppSettings seed = CreateSeed();
        seed.CopilotJsonOutputMigrated = true;
        Save(seed);

        AppSettings actual = new SettingsService(_directory, pnpmBinDir: string.Empty).Settings;

        actual.ReviewerLauncherArguments.Should().Be(seed.ReviewerLauncherArguments);
        actual.ReviewerLauncherResumeArguments.Should().Be(seed.ReviewerLauncherResumeArguments);
        actual.ReviewedLauncherArguments.Should().Be(seed.ReviewedLauncherArguments);
        actual.ReviewedLauncherResumeArguments.Should().Be(seed.ReviewedLauncherResumeArguments);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static AppSettings CreateSeed() => new()
    {
        LauncherSlotsMigrated = true,
        LauncherSkillPromptMigrated = true,
        LauncherPresetsMigrated = true,
        LauncherResumeTemplatesMigrated = true,
        ReviewerLauncherCommandPath = "copilot",
        ReviewerLauncherArguments = _reviewerArguments,
        ReviewerLauncherResumeArguments = _reviewerArguments + " --session-id {sessionId}",
        ReviewerLauncherPresetId = "copilot",
        ReviewedLauncherCommandPath = "copilot",
        ReviewedLauncherArguments = _reviewedArguments,
        ReviewedLauncherResumeArguments = _reviewedArguments + " --session-id {sessionId}",
        ReviewedLauncherPresetId = "copilot",
    };

    private void Save(AppSettings settings)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "settings.json"), JsonSerializer.Serialize(settings));
    }
}
