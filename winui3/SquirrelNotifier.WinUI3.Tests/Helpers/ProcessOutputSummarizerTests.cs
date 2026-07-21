// <copyright file="ProcessOutputSummarizerTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public class ProcessOutputSummarizerTests
{
    private static readonly SecretMasker _masker = new(["known-secret"]);

    [Theory]
    [InlineData("", "")]
    [InlineData("   \r\n  ", "")]
    [InlineData("single line", "single line")]
    [InlineData("  padded  ", "padded")]
    [InlineData("first\nsecond", "first | second")]
    [InlineData("a\r\nb\r\nc\r\nd\r\ne", "a | b | c")]
    public void Summarize_ShouldJoinLeadingLines(string input, string expected)
    {
        ProcessOutputSummarizer.Summarize(input, _masker).Should().Be(expected);
    }

    [Fact]
    public void Summarize_ShouldMaskSecretsAndStripAnsiControls()
    {
        string summary = ProcessOutputSummarizer.Summarize("\u001b[31mauth failed: known-secret\u001b[0m", _masker);

        summary.Should().Be("auth failed: ***");
    }

    [Fact]
    public void Summarize_ShouldTruncateLongOutput()
    {
        string summary = ProcessOutputSummarizer.Summarize(new string('x', 900), _masker);

        summary.Should().HaveLength(500);
    }
}
