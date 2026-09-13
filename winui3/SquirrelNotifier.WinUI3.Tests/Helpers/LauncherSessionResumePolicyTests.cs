// <copyright file="LauncherSessionResumePolicyTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public sealed class LauncherSessionResumePolicyTests
{
    [Theory]
    [InlineData("claude", "Reviewer", true)]
    [InlineData("claude", "Reviewed", true)]
    [InlineData("copilot", "Reviewer", true)]
    [InlineData("copilot", "Reviewed", true)]
    [InlineData("codex", "Reviewer", false)]
    [InlineData("agy", "Reviewed", false)]
    public void Evaluate_ShouldFollowPresetCapability(string presetId, string roleName, bool expected)
    {
        LauncherAgentDefinition definition = LauncherAgentCatalog.Find(presetId)!;
        LauncherRole role = Enum.Parse<LauncherRole>(roleName);
        string arguments = role == LauncherRole.Reviewer
            ? definition.ReviewerArgumentsTemplate
            : definition.ReviewedArgumentsTemplate;
        string resumeArguments = role == LauncherRole.Reviewer
            ? definition.ReviewerResumeArgumentsTemplate
            : definition.ReviewedResumeArgumentsTemplate;

        LauncherSessionResumeCapability result = LauncherSessionResumePolicy.Evaluate(
            definition.Command,
            arguments,
            resumeArguments,
            definition.Id,
            role);

        result.IsSupported.Should().Be(expected);
    }

    [Theory]
    [InlineData("claude", "--custom-prompt", "--resume {sessionId}", true, "claude")]
    [InlineData("my-agent", "--new {sessionId}", "--continue {sessionId}", true, "custom:MY-AGENT")]
    [InlineData("my-agent", "--new", "--continue {sessionId}", false, null)]
    [InlineData("my-agent", "--new {sessionId}", "--continue", false, null)]
    public void Evaluate_ShouldRequireDeterministicSessionIdSupplyForCustomSettings(
        string command,
        string arguments,
        string resumeArguments,
        bool expected,
        string? expectedAgentId)
    {
        LauncherSessionResumeCapability result = LauncherSessionResumePolicy.Evaluate(
            command,
            arguments,
            resumeArguments,
            LauncherAgentCatalog.CustomPresetId,
            LauncherRole.Reviewer);

        result.IsSupported.Should().Be(expected);
        result.AgentId.Should().Be(expectedAgentId);
    }

    [Fact]
    public void BuildApplied_ShouldUseOnlyFirstEightSessionIdCharacters()
    {
        Guid sessionId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");

        string result = SessionResumeMessageFormatter.BuildApplied(sessionId);

        result.Should().Be("resumed session 01234567…");
        result.Should().NotContain(sessionId.ToString("D"));
    }

    [Theory]
    [InlineData("NotFound", "保存済みエントリがありません")]
    [InlineData("Expired", "TTL が切れています")]
    [InlineData("UnsupportedPreset", "resume に対応していません")]
    [InlineData("WorkingDirectoryMismatch", "working directory が保存時と一致しません")]
    public void BuildUnavailable_ShouldDescribeReason(string reasonName, string expected)
    {
        SessionResumeUnavailableReason reason = Enum.Parse<SessionResumeUnavailableReason>(reasonName);

        SessionResumeMessageFormatter.BuildUnavailable(reason).Should().Contain(expected);
    }

    [Theory]
    [InlineData(true, "resume 対応")]
    [InlineData(false, "resume 非対応")]
    public void BuildCapabilityLabel_ShouldDescribeSupport(bool supported, string expected)
    {
        LauncherSessionResumeCapability capability = supported
            ? new LauncherSessionResumeCapability(SessionIdSupply.ClientAssigned, "claude", "--session-id {sessionId}")
            : new LauncherSessionResumeCapability(SessionIdSupply.None, null, string.Empty);

        SessionResumeMessageFormatter.BuildCapabilityLabel(capability).Should().StartWith(expected);
    }
}
