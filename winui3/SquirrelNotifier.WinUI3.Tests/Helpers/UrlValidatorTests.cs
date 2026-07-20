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

    [Theory]
    [InlineData("https://github.com/scottlz0310/squirrel-notifier/pull/51", "scottlz0310/squirrel-notifier", 51, true)]
    [InlineData("https://github.com/scottlz0310/squirrel-notifier/pull/51", "scottlz0310/another-repo", 51, false)] // repo mismatch
    [InlineData("https://github.com/scottlz0310/squirrel-notifier/pull/51", "scottlz0310/squirrel-notifier", 52, false)] // pr mismatch
    [InlineData("https://github.com/SCOTTLZ0310/SQUIRREL-NOTIFIER/pull/51", "scottlz0310/squirrel-notifier", 51, true)] // case insensitive
    [InlineData("http://github.com/scottlz0310/squirrel-notifier/pull/51", "scottlz0310/squirrel-notifier", 51, false)] // unsafe scheme
    [InlineData("", "repo", 1, false)]
    [InlineData(null, "repo", 1, false)]
    public void IsSafeGitHubUrl_WithRepoAndNumber_ShouldValidateCorrectly(string? url, string repository, int prNumber, bool expected)
    {
        // Act
        bool result = UrlValidator.IsSafeGitHubUrl(url, repository, prNumber);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("https://gateway.example/device", true)]
    [InlineData("http://127.0.0.1:8080/device?user_code=WDJB-MJHT", true)]
    [InlineData("HTTPS://gateway.example/device", true)] // scheme is case-insensitive
    [InlineData("ms-settings:privacy", false)] // OS protocol handler
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("ftp://gateway.example/device", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("/relative/path", false)] // not absolute
    [InlineData("gateway.example/device", false)] // missing scheme
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsHttpOrHttpsAbsoluteUrl_ShouldAllowOnlyHttpAndHttps(string? url, bool expected)
    {
        // Act
        bool result = UrlValidator.IsHttpOrHttpsAbsoluteUrl(url);

        // Assert
        result.Should().Be(expected);
    }
}
