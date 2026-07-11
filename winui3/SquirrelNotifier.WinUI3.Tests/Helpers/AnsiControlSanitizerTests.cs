// <copyright file="AnsiControlSanitizerTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public class AnsiControlSanitizerTests
{
    [Theory]
    [InlineData("\x1b[31m赤色\x1b[0m", "赤色")]
    [InlineData("\x1b[1;32;40mbold\x1b[m", "bold")]
    [InlineData("\x1b[2J\x1b[Hクリア", "クリア")]
    [InlineData("\x1b]0;window title\x07本文", "本文")]
    [InlineData("\x1b]8;;https://example.com\x1b\\リンク", "リンク")]
    [InlineData("\x1b[31", "")]
    [InlineData("\x1bMabc", "abc")]
    [InlineData("前\x1b[0K後", "前後")]
    public void Sanitize_ShouldRemoveAnsiEscapeSequences(string input, string expected)
    {
        AnsiControlSanitizer.Sanitize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("a\u0007b", "ab")]
    [InlineData("a\u0000b\u001Fc", "abc")]
    [InlineData("a\u007Fb", "ab")]
    [InlineData("a\rb", "ab")]
    public void Sanitize_ShouldRemoveControlCharacters(string input, string expected)
    {
        AnsiControlSanitizer.Sanitize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("タブ\tは保持", "タブ\tは保持")]
    [InlineData("普通のログ行です", "普通のログ行です")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Sanitize_ShouldPreserveTabAndPlainText(string? input, string expected)
    {
        AnsiControlSanitizer.Sanitize(input!).Should().Be(expected);
    }
}
