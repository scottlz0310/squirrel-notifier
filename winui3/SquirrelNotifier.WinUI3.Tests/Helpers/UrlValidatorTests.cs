// <copyright file="UrlValidatorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public class UrlValidatorTests
{
    [Theory]
    [InlineData("https://github.com/scottlz0310/squirrel-notifier/pull/51", true)]
    [InlineData("https://github.com/scottlz0310-user/another-repo/pull/123?query=param", true)]
    [InlineData("http://github.com/scottlz0310/squirrel-notifier/pull/51", false)] // http instead of https
    [InlineData("https://evil.com/github.com/scottlz0310/squirrel-notifier", false)] // wrong host
    [InlineData("https://github.com/scottlz0310/squirrel-notifier;rm -rf", false)] // unsafe characters
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSafeGitHubUrl_ShouldValidateCorrectly(string? url, bool expected)
    {
        // Act
        bool result = UrlValidator.IsSafeGitHubUrl(url);

        // Assert
        result.Should().Be(expected);
    }
}
