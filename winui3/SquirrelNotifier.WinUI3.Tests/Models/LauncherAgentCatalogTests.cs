// <copyright file="LauncherAgentCatalogTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Linq;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Models;

public class LauncherAgentCatalogTests
{
    [Fact]
    public void All_ShouldHaveUniqueIds()
    {
        LauncherAgentCatalog.All.Select(a => a.Id).Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData("claude", "claude-code")]
    [InlineData("codex", "codex")]
    [InlineData("agy", "agy")]
    [InlineData("copilot", null)]
    public void All_ShouldMapExpectedRateLimitAgentId(string presetId, string? expectedRateLimitAgentId)
    {
        LauncherAgentDefinition? definition = LauncherAgentCatalog.Find(presetId);

        definition.Should().NotBeNull();
        definition!.RateLimitAgentId.Should().Be(expectedRateLimitAgentId);
    }

    [Fact]
    public void Find_ShouldReturnNull_ForUnknownId()
    {
        LauncherAgentCatalog.Find("unknown-agent").Should().BeNull();
    }

    [Fact]
    public void AllWithCustomOption_ShouldAppendCustomPreset()
    {
        LauncherAgentCatalog.AllWithCustomOption.Should().HaveCount(LauncherAgentCatalog.All.Count + 1);
        LauncherAgentCatalog.AllWithCustomOption.Last().Id.Should().Be(LauncherAgentCatalog.CustomPresetId);
    }

    [Theory]
    [InlineData("claude", "-p \"/thread-owl-pr-reviewer {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\"", "reviewer", "claude")]
    [InlineData("claude", "-p \"/review-raven-thread-owl-cycle {owner}/{repo}#{prNumber} のレビュー指摘に対応してください\"", "reviewed", "claude")]
    [InlineData("claude", "--something-else", "reviewer", LauncherAgentCatalog.CustomPresetId)]
    [InlineData("unknown-cmd", "unknown-args", "reviewer", LauncherAgentCatalog.CustomPresetId)]
    public void ResolvePresetId_ShouldMatchExactCommandAndArguments(string command, string arguments, string roleName, string expectedPresetId)
    {
        LauncherRole role = roleName == "reviewer" ? LauncherRole.Reviewer : LauncherRole.Reviewed;

        LauncherAgentCatalog.ResolvePresetId(command, arguments, role).Should().Be(expectedPresetId);
    }

    [Fact]
    public void ResolvePresetId_ShouldNotCrossMatchReviewerTemplateForReviewedRole()
    {
        // reviewer 用テンプレートを reviewed ロールで判定した場合は一致しない（custom 扱い）
        LauncherAgentDefinition claude = LauncherAgentCatalog.Find("claude")!;

        LauncherAgentCatalog.ResolvePresetId(claude.Command, claude.ReviewerArgumentsTemplate, LauncherRole.Reviewed)
            .Should().Be(LauncherAgentCatalog.CustomPresetId);
    }
}
