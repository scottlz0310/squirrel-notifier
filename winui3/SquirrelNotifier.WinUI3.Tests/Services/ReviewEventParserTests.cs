// <copyright file="ReviewEventParserTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class ReviewEventParserTests
{
    [Fact]
    public void Parse_ValidJson_ShouldReturnReviewEvent()
    {
        // Arrange
        string json = "{\"eventId\":\"evt_1\",\"repository\":\"org/repo\",\"prNumber\":42,\"prUrl\":\"https://github.com/org/repo/pull/42\",\"reason\":\"test\",\"source\":\"src\",\"message\":\"msg\"}";

        // Act
        ReviewEvent? result = ReviewEventParser.Parse(json);

        // Assert
        result.Should().NotBeNull();
        result!.EventId.Should().Be("evt_1");
        result.Repository.Should().Be("org/repo");
        result.PrNumber.Should().Be(42);
        result.PrUrl.Should().Be("https://github.com/org/repo/pull/42");
        result.Reason.Should().Be("test");
        result.Source.Should().Be("src");
        result.Message.Should().Be("msg");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not a json")]
    [InlineData("{\"eventId\":\"evt_1\"}")] // missing repository and prUrl
    [InlineData("{\"eventId\":\"evt_1\",\"repository\":\"org/repo\",\"prUrl\":\"http://unsafe.com\"}")] // unsafe URL
    public void Parse_InvalidJson_ShouldReturnNull(string? json)
    {
        // Act
        ReviewEvent? result = ReviewEventParser.Parse(json);

        // Assert
        result.Should().BeNull();
    }
}
