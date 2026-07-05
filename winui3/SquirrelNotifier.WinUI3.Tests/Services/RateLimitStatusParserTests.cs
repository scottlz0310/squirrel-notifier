// <copyright file="RateLimitStatusParserTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class RateLimitStatusParserTests
{
    [Fact]
    public void Parse_ValidJson_ShouldReturnAllLimits()
    {
        string json = "{\"limits\":[" +
            "{\"id\":\"5h\",\"label\":\"5時間制限\",\"resetAt\":\"2026-07-05T20:00:00Z\"}," +
            "{\"id\":\"7d\",\"label\":\"7日間制限\",\"resetAt\":\"2026-07-10T00:00:00Z\"}]}";

        List<RateLimitInfo> result = RateLimitStatusParser.Parse(json);

        result.Should().HaveCount(2);
        result[0].Id.Should().Be("5h");
        result[0].Label.Should().Be("5時間制限");
        result[0].ResetAt.Should().Be(DateTimeOffset.Parse("2026-07-05T20:00:00Z"));
        result[1].Id.Should().Be("7d");
    }

    [Fact]
    public void Parse_EntryMissingId_ShouldSkipThatEntryOnly()
    {
        string json = "{\"limits\":[" +
            "{\"label\":\"IDなし\",\"resetAt\":\"2026-07-05T20:00:00Z\"}," +
            "{\"id\":\"7d\",\"label\":\"7日間制限\",\"resetAt\":\"2026-07-10T00:00:00Z\"}]}";

        List<RateLimitInfo> result = RateLimitStatusParser.Parse(json);

        result.Should().ContainSingle();
        result[0].Id.Should().Be("7d");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not a json")]
    [InlineData("{\"limits\":[]}")]
    [InlineData("{\"limits\":null}")]
    [InlineData("{\"other\":\"field\"}")]
    public void Parse_InvalidOrEmptyJson_ShouldReturnEmptyList(string? json)
    {
        List<RateLimitInfo> result = RateLimitStatusParser.Parse(json);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_WithSourceUri_ShouldSetSourceUriOnEachEntry()
    {
        const string SourceUri = "ratelimit://status/claude";
        string json = "{\"limits\":[{\"id\":\"5h\",\"label\":\"5時間制限\",\"resetAt\":\"2026-07-05T20:00:00Z\"}]}";

        List<RateLimitInfo> result = RateLimitStatusParser.Parse(json, SourceUri);

        result.Should().ContainSingle();
        result[0].SourceUri.Should().Be(SourceUri);
        result[0].ReminderKey.Should().Be($"{SourceUri}:5h");
    }
}
