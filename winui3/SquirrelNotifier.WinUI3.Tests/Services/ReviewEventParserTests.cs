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
        List<ReviewEvent> result = ReviewEventParser.Parse(json);

        // Assert
        result.Should().ContainSingle();
        ReviewEvent reviewEvent = result[0];
        reviewEvent.EventId.Should().Be("evt_1");
        reviewEvent.Repository.Should().Be("org/repo");
        reviewEvent.PrNumber.Should().Be(42);
        reviewEvent.PrUrl.Should().Be("https://github.com/org/repo/pull/42");
        reviewEvent.Reason.Should().Be("test");
        reviewEvent.Source.Should().Be("src");
        reviewEvent.Message.Should().Be("msg");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not a json")]
    [InlineData("{\"eventId\":\"evt_1\"}")] // missing repository and prUrl
    [InlineData("{\"eventId\":\"evt_1\",\"repository\":\"org/repo\",\"prUrl\":\"http://unsafe.com\"}")] // unsafe URL
    public void Parse_InvalidJson_ShouldReturnEmptyList(string? json)
    {
        // Act
        List<ReviewEvent> result = ReviewEventParser.Parse(json);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ValidArrayJson_ShouldReturnReviewEvent()
    {
        // Arrange
        string json = "[{\"owner\":\"org\",\"repo\":\"repo\",\"prNumber\":42,\"queuedAt\":\"2026-06-13T22:00:00Z\",\"reason\":\"test\",\"requestedBy\":\"src\"}]";

        // Act
        List<ReviewEvent> result = ReviewEventParser.Parse(json);

        // Assert
        result.Should().ContainSingle();
        ReviewEvent reviewEvent = result[0];
        reviewEvent.EventId.Should().Be("evt_org_repo_42_test_2026-06-13T22_00_00Z");
        reviewEvent.Repository.Should().Be("org/repo");
        reviewEvent.PrNumber.Should().Be(42);
        reviewEvent.PrUrl.Should().Be("https://github.com/org/repo/pull/42");
        reviewEvent.Reason.Should().Be("test");
        reviewEvent.Source.Should().Be("src");
        reviewEvent.Message.Should().Be("test by src");
    }

    [Theory]
    [InlineData("{\"eventId\":\"evt_1\",\"repository\":\"org/repo\",\"prNumber\":42,\"prUrl\":\"https://github.com/attacker/repo/pull/42\"}")] // repository mismatch in object
    [InlineData("{\"eventId\":\"evt_1\",\"repository\":\"org/repo\",\"prNumber\":42,\"prUrl\":\"https://github.com/org/repo/pull/43\"}")] // prNumber mismatch in object
    public void Parse_UnmatchedUrlInEvent_ShouldReturnEmptyList(string json)
    {
        // Act
        List<ReviewEvent> result = ReviewEventParser.Parse(json);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_RealPayloadSchema_ShouldSuccessfullyConstructReviewEvents()
    {
        // Arrange
        string json = @"[
            {
                ""owner"": ""scottlz0310"",
                ""repo"": ""squirrel-notifier"",
                ""prNumber"": 56,
                ""installationId"": 12345,
                ""queuedAt"": ""2026-06-13T22:00:00Z"",
                ""reason"": ""review requested"",
                ""requestedBy"": ""some-user"",
                ""sourceCommentId"": 3407917385
            },
            {
                ""owner"": ""scottlz0310"",
                ""repo"": ""squirrel-notifier"",
                ""prNumber"": 56,
                ""installationId"": 12345,
                ""queuedAt"": ""2026-06-13T22:05:00Z"",
                ""reason"": ""re-review requested""
            }
        ]";

        // Act
        List<ReviewEvent> result = ReviewEventParser.Parse(json);

        // Assert
        result.Should().HaveCount(2);

        ReviewEvent first = result[0];
        first.EventId.Should().Be("evt_scottlz0310_squirrel-notifier_56_review_requested_2026-06-13T22_00_00Z");
        first.Repository.Should().Be("scottlz0310/squirrel-notifier");
        first.PrNumber.Should().Be(56);
        first.PrUrl.Should().Be("https://github.com/scottlz0310/squirrel-notifier/pull/56");
        first.Reason.Should().Be("review requested");
        first.Source.Should().Be("some-user");
        first.Message.Should().Be("review requested by some-user");

        ReviewEvent second = result[1];
        second.EventId.Should().Be("evt_scottlz0310_squirrel-notifier_56_re-review_requested_2026-06-13T22_05_00Z");
        second.Repository.Should().Be("scottlz0310/squirrel-notifier");
        second.PrNumber.Should().Be(56);
        second.PrUrl.Should().Be("https://github.com/scottlz0310/squirrel-notifier/pull/56");
        second.Reason.Should().Be("re-review requested");
        second.Source.Should().Be("thread-owl");
        second.Message.Should().Be("re-review requested");
    }
}
