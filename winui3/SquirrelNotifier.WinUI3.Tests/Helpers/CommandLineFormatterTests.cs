// <copyright file="CommandLineFormatterTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public class CommandLineFormatterTests
{
    [Fact]
    public void Format_ShouldNotQuoteBareWords()
    {
        // Act
        string result = CommandLineFormatter.Format("claude", new[] { "--interactive", "--repo", "owner/repo" });

        // Assert
        result.Should().Be("claude --interactive --repo owner/repo");
    }

    [Fact]
    public void Format_ShouldQuoteArgumentsContainingSpaces()
    {
        // Act
        string result = CommandLineFormatter.Format("claude", new[] { "-p", "/thread-owl-pr-reviewer owner/repo#123 を opened モードでレビューしてください" });

        // Assert
        result.Should().Be("claude -p \"/thread-owl-pr-reviewer owner/repo#123 を opened モードでレビューしてください\"");
    }

    [Fact]
    public void Format_ShouldEscapeEmbeddedQuotes()
    {
        // Act
        string result = CommandLineFormatter.Format("claude", new[] { "-p", "say \"hello\" now" });

        // Assert
        result.Should().Be("claude -p \"say \\\"hello\\\" now\"");
    }

    [Fact]
    public void Format_ShouldQuoteEmptyArguments()
    {
        // Act
        string result = CommandLineFormatter.Format("claude", new[] { string.Empty });

        // Assert
        result.Should().Be("claude \"\"");
    }

    [Fact]
    public void Format_ShouldQuoteCommandPathContainingSpaces()
    {
        // Act
        string result = CommandLineFormatter.Format("C:\\Program Files\\claude\\claude.exe", new[] { "--version" });

        // Assert
        result.Should().Be("\"C:\\Program Files\\claude\\claude.exe\" --version");
    }

    [Fact]
    public void Format_ShouldReturnBareCommand_WhenNoArguments()
    {
        // Act
        string result = CommandLineFormatter.Format("claude", System.Array.Empty<string>());

        // Assert
        result.Should().Be("claude");
    }
}
