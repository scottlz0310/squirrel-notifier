// <copyright file="AgyModelSelectionServiceTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class AgyModelSelectionServiceTests
{
    [Theory]
    [InlineData("--model gemini-flash", "gemini-flash")]
    [InlineData("--model=claude-sonnet", "claude-sonnet")]
    [InlineData("--model \"Gemini 3.8 Flash (High)\"", "Gemini 3.8 Flash (High)")]
    [InlineData("--model gemini-flash --model=claude-sonnet", "claude-sonnet")]
    [InlineData("--model", null)]
    [InlineData("--model=", "")]
    public async Task ResolveAsync_ShouldPreferExplicitModelWithoutReadingSettings(string arguments, string? expected)
    {
        AgyModelSelectionService service = new(readSettings: (_, _) => throw new InvalidOperationException("設定を読むべきではありません"));

        AgyModelSelection result = await service.ResolveAsync(arguments, CancellationToken.None);

        result.Model.Should().Be(expected);
    }

    [Theory]
    [InlineData("-p \"prompt --model claude-sonnet\"")]
    [InlineData("-p \"--model=claude-sonnet\"")]
    [InlineData("--json-schema \"--model=claude-sonnet\"")]
    [InlineData("-- --model claude-sonnet")]
    [InlineData("")]
    public async Task ResolveAsync_ShouldReadPersistentModelWhenOverrideIsAbsent(string arguments)
    {
        AgyModelSelectionService service = new("settings-fixture.json", (path, _) =>
        {
            path.Should().Be("settings-fixture.json");
            return Task.FromResult("{\"model\":\"Gemini 3.8 Flash (High)\"}");
        });

        AgyModelSelection result = await service.ResolveAsync(arguments, CancellationToken.None);

        result.Model.Should().Be("Gemini 3.8 Flash (High)");
        result.Error.Should().BeNull();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"model\":null}")]
    [InlineData("{\"model\":123}")]
    [InlineData("[]")]
    [InlineData("not-json")]
    public async Task ResolveAsync_ShouldReportUnknownModelWhenSettingsAreInvalid(string json)
    {
        AgyModelSelectionService service = new(readSettings: (_, _) => Task.FromResult(json));

        AgyModelSelection result = await service.ResolveAsync(string.Empty, CancellationToken.None);

        result.Model.Should().BeNull();
        result.Error.Should().Contain("全枠");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolveAsync_ShouldReportReadFailureWithoutGuessingModel(bool denied)
    {
        AgyModelSelectionService service = new(readSettings: (_, _) => throw (denied
            ? new UnauthorizedAccessException("読み取り拒否") : new FileNotFoundException("設定がありません")));

        AgyModelSelection result = await service.ResolveAsync(string.Empty, CancellationToken.None);

        result.Model.Should().BeNull();
        result.Error.Should().Contain("取得できません");
    }

    [Fact]
    public async Task ResolveAsync_ShouldPropagateCancellation()
    {
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        AgyModelSelectionService service = new();

        Func<Task> act = () => service.ResolveAsync("--model gemini-flash", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("Reviewer", "gemini-flash")]
    [InlineData("Reviewed", "claude-sonnet")]
    public async Task ResolveConfiguredAsync_ShouldReadCorrectSlot(string roleName, string expected)
    {
        AppSettings settings = new()
        {
            SessionResumeEnabled = false,
            ReviewerLauncherArguments = "--model gemini-flash",
            ReviewedLauncherArguments = "--model claude-sonnet",
        };
        AgyModelSelectionService service = new(readSettings: (_, _) => throw new InvalidOperationException("明示モデルが優先されるべきです"));

        AgyModelSelection result = await service.ResolveConfiguredAsync(settings, Enum.Parse<LauncherRole>(roleName), CancellationToken.None);

        result.Model.Should().Be(expected);
    }

    [Theory]
    [InlineData("gemini-pro", "gemini-flash", false)]
    [InlineData("gemini-pro", "claude-sonnet", true)]
    [InlineData("custom-model", "claude-sonnet", true)]
    public async Task ResolveConfiguredAsync_ShouldUseAllQuotasWhenResumeFamilyDiffers(string normalModel, string resumeModel, bool expectedUnknown)
    {
        AppSettings settings = new()
        {
            SessionResumeEnabled = true,
            ReviewerLauncherCommandPath = "agy",
            ReviewerLauncherPresetId = "custom",
            ReviewerLauncherArguments = $"--model {normalModel} --session {{sessionId}}",
            ReviewerLauncherResumeArguments = $"--model {resumeModel} --conversation {{sessionId}}",
        };
        AgyModelSelectionService service = new(readSettings: (_, _) => throw new InvalidOperationException("明示モデルが優先されるべきです"));

        AgyModelSelection result = await service.ResolveConfiguredAsync(settings, LauncherRole.Reviewer, CancellationToken.None);

        result.Model.Should().Be(expectedUnknown ? null : normalModel);
        if (expectedUnknown)
        {
            result.Error.Should().Contain("全枠");
        }
    }
}
