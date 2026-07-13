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

    [Fact]
    public void Parse_NewSchemaWithUsedPercentage_ShouldPopulateUsedPercentage()
    {
        string json = "{\"schemaVersion\":1,\"agentId\":\"claude-code\",\"observedAt\":\"2026-07-11T10:00:00Z\"," +
            "\"limits\":[{\"id\":\"5h\",\"label\":\"5時間制限\",\"resetAt\":\"2026-07-05T20:00:00Z\",\"usedPercentage\":73}]}";

        List<RateLimitInfo> result = RateLimitStatusParser.Parse(json);

        result.Should().ContainSingle();
        result[0].UsedPercentage.Should().Be(73);
    }

    [Fact]
    public void Parse_LegacyResetAtOnlySchema_ShouldStillWork_WithNullUsedPercentage()
    {
        // 旧形式（schemaVersion 等を持たない resetAt-only payload）の後方互換
        string json = "{\"limits\":[{\"id\":\"5h\",\"label\":\"5時間制限\",\"resetAt\":\"2026-07-05T20:00:00Z\"}]}";

        List<RateLimitInfo> result = RateLimitStatusParser.Parse(json);

        result.Should().ContainSingle();
        result[0].UsedPercentage.Should().BeNull();
    }

    [Fact]
    public void ParseSnapshot_NewSchema_ShouldReturnSnapshotWithMetadata()
    {
        string json = "{\"schemaVersion\":1,\"agentId\":\"claude-code\",\"observedAt\":\"2026-07-11T10:00:00Z\"," +
            "\"limits\":[{\"id\":\"5h\",\"label\":\"5時間制限\",\"resetAt\":\"2026-07-05T20:00:00Z\",\"usedPercentage\":73}]}";

        RateLimitSnapshot? snapshot = RateLimitStatusParser.ParseSnapshot(json);

        snapshot.Should().NotBeNull();
        snapshot!.AgentId.Should().Be("claude-code");
        snapshot.ObservedAt.Should().Be(DateTimeOffset.Parse("2026-07-11T10:00:00Z"));
        snapshot.Limits.Should().ContainSingle();
        snapshot.Limits[0].UsedPercentage.Should().Be(73);
    }

    [Theory]
    [InlineData("{\"limits\":[{\"id\":\"5h\",\"label\":\"5時間制限\",\"resetAt\":\"2026-07-05T20:00:00Z\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"limits\":[]}")]
    [InlineData("{\"agentId\":\"claude-code\",\"limits\":[]}")]
    [InlineData("{\"observedAt\":\"2026-07-11T10:00:00Z\",\"limits\":[]}")]
    [InlineData("")]
    [InlineData((string?)null)]
    [InlineData("not a json")]
    public void ParseSnapshot_LegacyOrIncompleteOrMalformedPayload_ShouldReturnNull(string? json)
    {
        // 旧形式（resetAt-only）や schemaVersion/agentId/observedAt のいずれかが欠けた
        // payload、malformed JSON はすべて「使用率判定の対象外」として null を返す
        RateLimitStatusParser.ParseSnapshot(json).Should().BeNull();
    }

    [Theory]
    [InlineData("{\"limits\":[{\"id\":\"5h\",\"label\":\"5時間制限\",\"resetAt\":\"2026-07-05T20:00:00Z\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"limits\":[{\"id\":\"5h\",\"label\":\"5時間制限\",\"resetAt\":\"2026-07-05T20:00:00Z\"}]}")]
    [InlineData("{\"agentId\":\"claude-code\",\"limits\":[{\"id\":\"5h\",\"label\":\"5時間制限\",\"resetAt\":\"2026-07-05T20:00:00Z\"}]}")]
    public void IsLegacySchema_ResetAtOnlyPayloadWithLimits_ShouldReturnTrue(string json)
    {
        RateLimitStatusParser.IsLegacySchema(json).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not a json")]
    [InlineData("{\"limits\":[]}")]
    [InlineData("{\"other\":\"field\"}")]
    public void IsLegacySchema_EmptyOrMalformedPayload_ShouldReturnFalse(string? json)
    {
        // limits が空/欠落・malformed JSON は「旧形式の実データがある」とは言えないため false
        RateLimitStatusParser.IsLegacySchema(json).Should().BeFalse();
    }

    [Fact]
    public void IsLegacySchema_NewSchemaPayload_ShouldReturnFalse()
    {
        string json = "{\"schemaVersion\":1,\"agentId\":\"claude-code\",\"observedAt\":\"2026-07-11T10:00:00Z\"," +
            "\"limits\":[{\"id\":\"5h\",\"label\":\"5時間制限\",\"resetAt\":\"2026-07-05T20:00:00Z\",\"usedPercentage\":73}]}";

        RateLimitStatusParser.IsLegacySchema(json).Should().BeFalse();
    }

    [Fact]
    public void ParseSnapshot_EntryFailingValidation_ShouldSkipThatEntryOnly()
    {
        string json = "{\"schemaVersion\":1,\"agentId\":\"claude-code\",\"observedAt\":\"2026-07-11T10:00:00Z\",\"limits\":[" +
            "{\"label\":\"IDなし\",\"resetAt\":\"2026-07-05T20:00:00Z\"}," +
            "{\"id\":\"7d\",\"label\":\"7日間制限\",\"resetAt\":\"2026-07-10T00:00:00Z\",\"usedPercentage\":45}]}";

        RateLimitSnapshot? snapshot = RateLimitStatusParser.ParseSnapshot(json);

        snapshot.Should().NotBeNull();
        snapshot!.Limits.Should().ContainSingle();
        snapshot.Limits[0].Id.Should().Be("7d");
    }
}
