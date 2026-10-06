// <copyright file="AgyAutoPausePolicyTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public sealed class AgyAutoPausePolicyTests
{
    [Theory]
    [InlineData("Gemini 3.8 Flash (High)", "gemini-weekly", true)]
    [InlineData("gemini-3.8-flash-high", "3p-5h", false)]
    [InlineData("CLAUDE SONNET", "3p-weekly", true)]
    [InlineData("claude-sonnet", "gemini-5h", false)]
    [InlineData("gpt-oss-120b", "3p-5h", true)]
    [InlineData("GPT OSS", "gemini-weekly", false)]
    [InlineData("geminiextra", "3p-5h", true)]
    [InlineData("custom", "gemini-5h", true)]
    [InlineData(null, "3p-weekly", true)]
    [InlineData("gemini", "gemini-5h", true)]
    public void IncludesLimit_ShouldSelectModelFamilyOrKeepAllWhenUnknown(string? model, string limitId, bool expected)
        => AgyAutoPausePolicy.IncludesLimit(model, limitId).Should().Be(expected);
}
