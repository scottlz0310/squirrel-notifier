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
    public void Format_ShouldPreserveSkillInvocationInQuotedPrompt()
    {
        string result = CommandLineFormatter.Format(
            "codex",
            new[]
            {
                "exec",
                "--skip-git-repo-check",
                "--json",
                "$thread-owl-pr-reviewer owner/repo#123 を opened モードでレビューしてください",
            });

        result.Should().Be(
            "codex exec --skip-git-repo-check --json '$thread-owl-pr-reviewer owner/repo#123 を opened モードでレビューしてください'");
    }

    // PowerShell の二重引用符内では $ / ` が展開・エスケープとして解釈されるため、
    // これらを含む引数は単一引用符で literal にする（#395）
    [Theory]
    [InlineData("$review-raven-thread-owl-cycle", "'$review-raven-thread-owl-cycle'")]
    [InlineData("$skill owner/repo#1", "'$skill owner/repo#1'")]
    [InlineData("a`b", "'a`b'")]
    [InlineData("$skill it's \"quoted\"", "'$skill it''s \"quoted\"'")]
    public void Format_ShouldUsePowerShellSingleQuotes_WhenArgumentContainsExpansionChars(string argument, string expected)
    {
        string result = CommandLineFormatter.Format("codex", new[] { "exec", argument });

        result.Should().Be($"codex exec {expected}");
    }

    [Fact]
    public void Format_ShouldEscapeEmbeddedQuotes()
    {
        // Act
        string result = CommandLineFormatter.Format("claude", new[] { "-p", "say \"hello\" now" });

        // Assert
        // "" (ダブルクォート二重化) は cmd.exe / PowerShell の双方で単一引数として
        // 再解釈される（実機検証済み）。CRT 形式の \" は PowerShell では分裂する。
        result.Should().Be("claude -p \"say \"\"hello\"\" now\"");
    }

    [Fact]
    public void Format_ShouldQuoteEmbeddedQuotes_WithoutWhitespace()
    {
        // Act
        string result = CommandLineFormatter.Format("claude", new[] { "-p", "hello\"world" });

        // Assert
        result.Should().Be("claude -p \"hello\"\"world\"");
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
