// <copyright file="LauncherPresetCoordinatorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class LauncherPresetCoordinatorTests
{
    [Theory]
    [InlineData("Reviewer")]
    [InlineData("Reviewed")]
    public void TryApply_ShouldProvideRoleSpecificArguments(string roleName)
    {
        LauncherAgentDefinition claude = LauncherAgentCatalog.Find("claude")!;
        LauncherRole role = Enum.Parse<LauncherRole>(roleName);
        var coordinator = new LauncherPresetCoordinator();
        string? command = null;
        string? arguments = null;
        string? resumeArguments = null;
        bool wasApplying = false;

        bool result = coordinator.TryApply(
            claude,
            role,
            values =>
            {
                command = values.Command;
                arguments = values.Arguments;
                resumeArguments = values.ResumeArguments;
                wasApplying = coordinator.IsApplying;
            });

        result.Should().BeTrue();
        command.Should().Be(claude.Command);
        arguments.Should().Be(role == LauncherRole.Reviewer
            ? claude.ReviewerArgumentsTemplate
            : claude.ReviewedArgumentsTemplate);
        resumeArguments.Should().Be(role == LauncherRole.Reviewer
            ? claude.ReviewerResumeArgumentsTemplate
            : claude.ReviewedResumeArgumentsTemplate);
        wasApplying.Should().BeTrue();
        coordinator.IsApplying.Should().BeFalse();
    }

    [Fact]
    public void TryApply_ShouldSkipCustomPreset()
    {
        var coordinator = new LauncherPresetCoordinator();
        bool callbackCalled = false;

        bool result = coordinator.TryApply(
            LauncherAgentCatalog.CustomPreset,
            LauncherRole.Reviewer,
            _ => callbackCalled = true);

        result.Should().BeFalse();
        callbackCalled.Should().BeFalse();
        coordinator.IsApplying.Should().BeFalse();
    }

    [Fact]
    public void TryApply_ShouldResetStateWhenApplyFails()
    {
        var coordinator = new LauncherPresetCoordinator();

        Action act = () => coordinator.TryApply(
            LauncherAgentCatalog.Find("claude"),
            LauncherRole.Reviewer,
            _ => throw new InvalidOperationException("test"));

        act.Should().Throw<InvalidOperationException>();
        coordinator.IsApplying.Should().BeFalse();
    }

    [Fact]
    public void TrySynchronizeSelection_ShouldSetKnownPresetAndProtectCallback()
    {
        var coordinator = new LauncherPresetCoordinator();
        LauncherAgentDefinition? current = LauncherAgentCatalog.CustomPreset;
        bool wasSynchronizing = false;

        bool result = coordinator.TrySynchronizeSelection(
            "claude",
            () => current,
            match =>
            {
                current = match;
                wasSynchronizing = coordinator.IsSynchronizing;
            });

        result.Should().BeTrue();
        current.Should().Be(LauncherAgentCatalog.Find("claude"));
        wasSynchronizing.Should().BeTrue();
        coordinator.IsSynchronizing.Should().BeFalse();
    }

    [Fact]
    public void TrySynchronizeSelection_ShouldSkipWhenSelectionAlreadyMatches()
    {
        var coordinator = new LauncherPresetCoordinator();
        LauncherAgentDefinition claude = LauncherAgentCatalog.Find("claude")!;
        bool callbackCalled = false;

        bool result = coordinator.TrySynchronizeSelection(
            "claude",
            () => claude,
            _ => callbackCalled = true);

        result.Should().BeFalse();
        callbackCalled.Should().BeFalse();
    }

    [Fact]
    public void TrySynchronizeSelection_ShouldSetNullForUnknownPreset()
    {
        var coordinator = new LauncherPresetCoordinator();
        LauncherAgentDefinition? current = LauncherAgentCatalog.Find("claude");

        bool result = coordinator.TrySynchronizeSelection(
            "unknown",
            () => current,
            match => current = match);

        result.Should().BeTrue();
        current.Should().BeNull();
        coordinator.IsSynchronizing.Should().BeFalse();
    }
}
