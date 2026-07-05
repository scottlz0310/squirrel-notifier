// <copyright file="PrReferenceParserTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public class PrReferenceParserTests
{
    [Theory]
    [InlineData("https://github.com/scottlz0310/squirrel-notifier/pull/123", "scottlz0310", "squirrel-notifier", 123)]
    [InlineData("https://github.com/scottlz0310/squirrel-notifier/pull/123/", "scottlz0310", "squirrel-notifier", 123)] // trailing slash
    [InlineData("https://github.com/scottlz0310/squirrel-notifier/pull/123/files", "scottlz0310", "squirrel-notifier", 123)] // files tab
    [InlineData("https://github.com/scottlz0310/squirrel-notifier/pull/123?diff=split", "scottlz0310", "squirrel-notifier", 123)] // query
    [InlineData("https://github.com/scottlz0310/squirrel-notifier/pull/123#discussion_r1", "scottlz0310", "squirrel-notifier", 123)] // fragment
    [InlineData("  https://github.com/scottlz0310/squirrel-notifier/pull/123  ", "scottlz0310", "squirrel-notifier", 123)] // surrounding whitespace
    [InlineData("scottlz0310/squirrel-notifier#123", "scottlz0310", "squirrel-notifier", 123)] // shorthand
    [InlineData("  scottlz0310/squirrel-notifier#123  ", "scottlz0310", "squirrel-notifier", 123)] // shorthand w/ whitespace
    public void TryParse_ShouldParseValidReferences(string input, string expectedOwner, string expectedRepo, int expectedPrNumber)
    {
        // Act
        bool success = PrReferenceParser.TryParse(input, out PrReference? reference);

        // Assert
        success.Should().BeTrue();
        reference.Should().NotBeNull();
        reference!.Owner.Should().Be(expectedOwner);
        reference.Repo.Should().Be(expectedRepo);
        reference.PrNumber.Should().Be(expectedPrNumber);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com/owner/repo/pull/123")] // wrong host
    [InlineData("http://github.com/owner/repo/pull/123")] // wrong scheme
    [InlineData("https://github.com/owner/repo/issues/123")] // not a PR path
    [InlineData("https://github.com/owner/repo/pull/0")] // zero is not a valid PR number
    [InlineData("https://github.com/owner/repo/pull/abc")] // non-numeric
    [InlineData("owner/repo")] // missing #number
    [InlineData("owner/repo#")] // missing number
    [InlineData("owner#123")] // missing repo segment
    [InlineData("owner/repo#123; rm -rf")] // unsafe trailing characters
    public void TryParse_ShouldRejectInvalidReferences(string? input)
    {
        // Act
        bool success = PrReferenceParser.TryParse(input, out PrReference? reference);

        // Assert
        success.Should().BeFalse();
        reference.Should().BeNull();
    }
}
