// <copyright file="LauncherSelectionArgumentsTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public class LauncherSelectionArgumentsTests
{
    [Theory]
    [InlineData("agy", "--model gemini-flash --effort high", "gemini-flash", "high")]
    [InlineData("claude", "--model=sonnet --effort=medium", "sonnet", "medium")]
    [InlineData("copilot", "--model gpt-6 --reasoning-effort high", "gpt-6", "high")]
    [InlineData("codex", "exec -m gpt-6 -c model_reasoning_effort=high", "gpt-6", "high")]
    [InlineData("codex", "exec --config=model='gpt-6' --config=model_reasoning_effort='xhigh'", "gpt-6", "xhigh")]
    [InlineData("codex", "-m chosen -c model=other", "chosen", null)]
    [InlineData("claude", "--model old --model new", "new", null)]
    [InlineData("agy", "-p \"--model=wrong\" --model right", "right", null)]
    [InlineData("claude", "--prompt \"--effort=wrong\" --effort high", null, "high")]
    [InlineData("codex", "exec -- --model=wrong", null, null)]
    [InlineData("codex", "exec \"説明文 --model=wrong\"", null, null)]
    [InlineData("codex", "resume session-id -m resumed", "resumed", null)]
    [InlineData("claude", "--fallback-model other --model chosen", "chosen", null)]
    [InlineData("codex", "exec \"説明文 --model=wrong\" --model chosen", "chosen", null)]
    [InlineData("claude", "--resume --model chosen", "chosen", null)]
    public void Parse_ShouldResolveSelectionsWithoutReadingPrompt(string agent, string arguments, string? model, string? effort)
    {
        LauncherSelectionArguments result = LauncherSelectionArguments.Parse(agent, McpSubscriptionService.ParseArguments(arguments));

        result.Model.Should().Be(model);
        result.Effort.Should().Be(effort);
    }

    [Theory]
    [InlineData("-p review --ignore-user-config", "review", true)]
    [InlineData("--profile=review", "review", false)]
    [InlineData("--model", null, false)]
    public void Parse_ShouldCaptureConfigSelection(string arguments, string? profile, bool ignoreUserConfig)
    {
        LauncherSelectionArguments result = LauncherSelectionArguments.Parse("codex", McpSubscriptionService.ParseArguments(arguments));

        result.Profile.Should().Be(profile);
        result.IgnoreUserConfig.Should().Be(ignoreUserConfig);
    }
}
