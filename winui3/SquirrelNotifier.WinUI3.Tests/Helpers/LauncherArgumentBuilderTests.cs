// <copyright file="LauncherArgumentBuilderTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public class LauncherArgumentBuilderTests
{
    [Fact]
    public void BuildArguments_ShouldSubstitutePlaceholdersSafely()
    {
        // Arrange
        var reviewEvent = new ReviewEvent
        {
            EventId = "test-event-1",
            Repository = "scottlz0310/squirrel-notifier",
            PrNumber = 52,
            PrUrl = "https://github.com/scottlz0310/squirrel-notifier/pull/52",
            Message = "Test message"
        };
        string template = "review --repo {owner}/{repo} --pr {prNumber} --url {prUrl}";

        // Act
        List<string> result = LauncherArgumentBuilder.BuildArguments(template, reviewEvent);

        // Assert
        result.Should().HaveCount(7);
        result[0].Should().Be("review");
        result[1].Should().Be("--repo");
        result[2].Should().Be("scottlz0310/squirrel-notifier");
        result[3].Should().Be("--pr");
        result[4].Should().Be("52");
        result[5].Should().Be("--url");
        result[6].Should().Be("https://github.com/scottlz0310/squirrel-notifier/pull/52");
    }

    [Theory]
    [InlineData("invalid;owner/repo")]
    [InlineData("owner/repo&bad")]
    [InlineData("owner/repo|cmd")]
    [InlineData("owner/repo;rm")]
    public void BuildArguments_ShouldThrowArgumentExceptionForUnsafeRepositoryNames(string unsafeRepo)
    {
        // Arrange
        var reviewEvent = new ReviewEvent
        {
            EventId = "test-event-2",
            Repository = unsafeRepo,
            PrNumber = 52,
            PrUrl = "https://github.com/scottlz0310/squirrel-notifier/pull/52",
            Message = "Test message"
        };
        string template = "--repo {owner}/{repo}";

        // Act
        Action act = () => LauncherArgumentBuilder.BuildArguments(template, reviewEvent);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildArguments_ShouldThrowArgumentExceptionForInvalidPrUrl()
    {
        // Arrange
        var reviewEvent = new ReviewEvent
        {
            EventId = "test-event-3",
            Repository = "scottlz0310/squirrel-notifier",
            PrNumber = 52,
            PrUrl = "https://malicious.com/pull/52",
            Message = "Test message"
        };
        string template = "--url {prUrl}";

        // Act
        Action act = () => LauncherArgumentBuilder.BuildArguments(template, reviewEvent);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildArguments_ShouldReturnEmptyListIfTemplateIsEmpty()
    {
        // Arrange
        var reviewEvent = new ReviewEvent
        {
            EventId = "test-event-4",
            Repository = "scottlz0310/squirrel-notifier",
            PrNumber = 52,
            PrUrl = "https://github.com/scottlz0310/squirrel-notifier/pull/52",
            Message = "Test message"
        };

        // Act
        List<string> result1 = LauncherArgumentBuilder.BuildArguments(string.Empty, reviewEvent);
        List<string> result2 = LauncherArgumentBuilder.BuildArguments(null!, reviewEvent);

        // Assert
        result1.Should().BeEmpty();
        result2.Should().BeEmpty();
    }
}
