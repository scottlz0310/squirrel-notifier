// <copyright file="ReviewEventParserTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public class ReviewEventParserTests
{
    private const string _cycleId = "cyc_01jbq3x8n5r7t9v2w4y6z8a0c1";
    private const string _candidateId = "cand_01jbq3x8n5r7t9v2w4y6z8a0d1";
    private const string _headSha = "0123456789abcdef0123456789abcdef01234567";

    // 受信形式: 候補の配列（キューの資源）、単一の候補、完成済みの ReviewEvent
    public static TheoryData<string> Formats => ["array", "single", "event"];

    public static TheoryData<string, string> MalformedCyclesInEveryFormat
    {
        get
        {
            string[] malformed =
            [
                "\"cycle\":null",
                "\"cycle\":\"invalid\"",
                "\"cycle\":[]",
                "\"cycle\":{}",
                "\"cycle\":{\"candidateId\":\"cand_01jbq3x8n5r7t9v2w4y6z8a0d1\",\"cycleId\":\"cyc_01jbq3x8n5r7t9v2w4y6z8a0c1\",\"round\":1}",
                "\"cycle\":{\"candidateId\":\"cand_01jbq3x8n5r7t9v2w4y6z8a0d1\",\"cycleId\":\"cyc_01jbq3x8n5r7t9v2w4y6z8a0c1\",\"round\":0,\"headSha\":\"0123456789abcdef0123456789abcdef01234567\"}",
                "\"cycle\":{\"candidateId\":\"cand_01jbq3x8n5r7t9v2w4y6z8a0d1\",\"cycleId\":\"cyc_01jbq3x8n5r7t9v2w4y6z8a0c1\",\"round\":\"1\",\"headSha\":\"0123456789abcdef0123456789abcdef01234567\"}",
                "\"cycle\":{\"round\":\"1\"}",
                "\"cycle\":{\"candidateId\":\"bad\",\"cycleId\":\"bad\",\"round\":0,\"headSha\":\"bad\"}",
                "\"cycle\":{\"candidateId\":\"cand_BAD\",\"cycleId\":\"cyc_01jbq3x8n5r7t9v2w4y6z8a0c1\",\"round\":1,\"headSha\":\"0123456789abcdef0123456789abcdef01234567\"}",
                "\"cycle\":{\"candidateId\":\"cand_01jbq3x8n5r7t9v2w4y6z8a0d1\",\"cycleId\":\"cyc_01jbq3x8n5r7t9v2w4y6z8a0c1\",\"round\":1,\"headSha\":\"0123456789ABCDEF0123456789abcdef01234567\"}",
                "\"cycle\":{\"candidateId\":\"cand_01jbq3x8n5r7t9v2w4y6z8a0d1\",\"cycleId\":\"cyc_01jbq3x8n5r7t9v2w4y6z8a0c1\",\"round\":1,\"headSha\":\"0123456\"}",
            ];
            TheoryData<string, string> data = [];
            foreach (string format in new[] { "array", "single", "event" })
            {
                foreach (string cycleJson in malformed)
                {
                    data.Add(format, cycleJson);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Parse_CandidateWithCycle_ShouldCarryTheCycleToTheEvent_InEveryFormat(string format)
    {
        string json = PayloadJson(format, $$"""
            "cycle":{"candidateId":"{{_candidateId}}","cycleId":"{{_cycleId}}","round":2,"headSha":"{{_headSha}}"}
            """);

        ReviewEventParseResult result = ReviewEventParser.Parse(json);

        result.Status.Should().Be(ReviewEventParseStatus.Parsed);
        result.Events.Single().Cycle.Should().Be(new ReviewCycleRef(_candidateId, _cycleId, 2, _headSha));
    }

    [Fact]
    public void ReviewEvent_ShouldKeepItsCycleWhenSerializedAndReadBack()
    {
        ReviewCycleRef cycle = new(_candidateId, _cycleId, 3, _headSha);
        ReviewEvent original = new()
        {
            EventId = "evt_1",
            Repository = "org/repo",
            PrNumber = 42,
            PrUrl = "https://github.com/org/repo/pull/42",
            Reason = "opened",
            Cycle = cycle,
        };

        string json = System.Text.Json.JsonSerializer.Serialize(original);
        ReviewEvent? restored = System.Text.Json.JsonSerializer.Deserialize<ReviewEvent>(json);

        restored!.Cycle.Should().Be(cycle);
        json.Should().Contain("\"cycle\":{\"candidateId\":\"" + _candidateId + "\",\"cycleId\":\"" + _cycleId + "\",\"round\":3,\"headSha\":\"" + _headSha + "\"}");
    }

    [Fact]
    public void Parse_CandidateWithoutCycle_ShouldLeaveTheCycleEmpty()
    {
        ReviewEventParseResult result = ReviewEventParser.Parse(CandidateJson(null));

        result.Status.Should().Be(ReviewEventParseStatus.Parsed);
        result.Events.Single().Cycle.Should().BeNull();
    }

    // 契約に合わない cycle は、受信形式に関わらず、観測を相関できないものとして捨てる。候補（キュー全体）は壊さない
    [Theory]
    [MemberData(nameof(MalformedCyclesInEveryFormat))]
    public void Parse_CandidateWithAMalformedCycle_ShouldKeepTheEventWithoutIt_InEveryFormat(string format, string cycleJson)
    {
        ReviewEventParseResult result = ReviewEventParser.Parse(PayloadJson(format, cycleJson));

        result.Status.Should().Be(ReviewEventParseStatus.Parsed);
        ReviewEvent reviewEvent = result.Events.Single();
        reviewEvent.Cycle.Should().BeNull();
        reviewEvent.PrNumber.Should().Be(42);
    }

    [Fact]
    public void Parse_QueueWithLegacyAndRavenCandidates_ShouldKeepEachCandidatesOwnCycle()
    {
        string raven = $$"""
            "cycle":{"candidateId":"{{_candidateId}}","cycleId":"{{_cycleId}}","round":1,"headSha":"{{_headSha}}"}
            """;
        string json = $"[{Candidate(raven, 42)},{Candidate(null, 43)}]";

        ReviewEventParseResult result = ReviewEventParser.Parse(json);

        result.Events.Should().HaveCount(2);
        result.Events[0].Cycle.Should().NotBeNull();
        result.Events[1].Cycle.Should().BeNull();
    }

    [Fact]
    public void Parse_ValidJson_ShouldReturnReviewEvent()
    {
        // Arrange
        string json = "{\"eventId\":\"evt_1\",\"repository\":\"org/repo\",\"prNumber\":42,\"prUrl\":\"https://github.com/org/repo/pull/42\",\"reason\":\"test\",\"source\":\"src\",\"message\":\"msg\"}";

        // Act
        ReviewEventParseResult result = ReviewEventParser.Parse(json);

        // Assert
        result.Status.Should().Be(ReviewEventParseStatus.Parsed);
        result.Events.Should().ContainSingle();
        ReviewEvent reviewEvent = result.Events[0];
        reviewEvent.EventId.Should().Be("evt_1");
        reviewEvent.Repository.Should().Be("org/repo");
        reviewEvent.PrNumber.Should().Be(42);
        reviewEvent.PrUrl.Should().Be("https://github.com/org/repo/pull/42");
        reviewEvent.Reason.Should().Be("test");
        reviewEvent.Source.Should().Be("src");
        reviewEvent.Message.Should().Be("msg");
    }

    [Theory]
    [InlineData("[]")] // 空キュー: 候補が無い正常状態（#230）
    [InlineData("[ ]")]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_EmptyPayload_ShouldBeParsedWithoutEvents(string? json)
    {
        // Act
        ReviewEventParseResult result = ReviewEventParser.Parse(json);

        // Assert
        result.Status.Should().Be(ReviewEventParseStatus.Parsed);
        result.Events.Should().BeEmpty();
    }

    [Theory]
    [InlineData("not a json")]
    [InlineData("{\"eventId\":\"evt_1\"}")] // missing repository and prUrl
    [InlineData("{\"eventId\":\"evt_1\",\"repository\":\"org/repo\",\"prUrl\":\"http://unsafe.com\"}")] // unsafe URL
    [InlineData("[{\"owner\":\"\",\"repo\":\"\",\"prNumber\":1}]")] // 配列だが 1 件も構築できない
    [InlineData("[{\"owner\":\"org\"")] // 途中で切れた JSON
    public void Parse_InvalidPayload_ShouldBeMalformed(string? json)
    {
        // Act
        ReviewEventParseResult result = ReviewEventParser.Parse(json);

        // Assert
        result.Status.Should().Be(ReviewEventParseStatus.Malformed);
        result.Events.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ValidArrayJson_ShouldReturnReviewEvent()
    {
        // Arrange
        string json = "[{\"owner\":\"org\",\"repo\":\"repo\",\"prNumber\":42,\"queuedAt\":\"2026-06-13T22:00:00Z\",\"reason\":\"test\",\"requestedBy\":\"src\"}]";

        // Act
        ReviewEventParseResult result = ReviewEventParser.Parse(json);

        // Assert
        result.Status.Should().Be(ReviewEventParseStatus.Parsed);
        result.Events.Should().ContainSingle();
        ReviewEvent reviewEvent = result.Events[0];
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
    public void Parse_UnmatchedUrlInEvent_ShouldBeMalformed(string json)
    {
        // Act
        ReviewEventParseResult result = ReviewEventParser.Parse(json);

        // Assert
        result.Status.Should().Be(ReviewEventParseStatus.Malformed);
        result.Events.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ArrayWithSomeUnusableCandidates_ShouldReturnUsableEvents()
    {
        // Arrange: 1 件目は owner が空で構築できないが、2 件目は妥当
        string json = @"[
            { ""owner"": """", ""repo"": ""repo"", ""prNumber"": 1, ""reason"": ""broken"" },
            { ""owner"": ""org"", ""repo"": ""repo"", ""prNumber"": 42, ""queuedAt"": ""2026-06-13T22:00:00Z"", ""reason"": ""test"" }
        ]";

        // Act
        ReviewEventParseResult result = ReviewEventParser.Parse(json);

        // Assert
        result.Status.Should().Be(ReviewEventParseStatus.Parsed);
        result.Events.Should().ContainSingle();
        result.Events[0].PrNumber.Should().Be(42);
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
        ReviewEventParseResult result = ReviewEventParser.Parse(json);

        // Assert
        result.Status.Should().Be(ReviewEventParseStatus.Parsed);
        result.Events.Should().HaveCount(2);

        ReviewEvent first = result.Events[0];
        first.EventId.Should().Be("evt_scottlz0310_squirrel-notifier_56_review_requested_2026-06-13T22_00_00Z");
        first.Repository.Should().Be("scottlz0310/squirrel-notifier");
        first.PrNumber.Should().Be(56);
        first.PrUrl.Should().Be("https://github.com/scottlz0310/squirrel-notifier/pull/56");
        first.Reason.Should().Be("review requested");
        first.Source.Should().Be("some-user");
        first.Message.Should().Be("review requested by some-user");

        ReviewEvent second = result.Events[1];
        second.EventId.Should().Be("evt_scottlz0310_squirrel-notifier_56_re-review_requested_2026-06-13T22_05_00Z");
        second.Repository.Should().Be("scottlz0310/squirrel-notifier");
        second.PrNumber.Should().Be(56);
        second.PrUrl.Should().Be("https://github.com/scottlz0310/squirrel-notifier/pull/56");
        second.Reason.Should().Be("re-review requested");
        second.Source.Should().Be("thread-owl");
        second.Message.Should().Be("re-review requested");
    }

    [Fact]
    public void Parse_WithSourceUri_ShouldOverrideSourceInCandidateFormat()
    {
        // Arrange: ReviewCandidate 形式（requestedBy なし）
        string json = @"[{
            ""owner"": ""scottlz0310"",
            ""repo"": ""squirrel-notifier"",
            ""prNumber"": 42,
            ""queuedAt"": ""2026-06-28T10:00:00Z"",
            ""reason"": ""re-review-requested""
        }]";

        // Act
        ReviewEventParseResult result = ReviewEventParser.Parse(json, "queue://review/re-review-requests");

        // Assert
        result.Events.Should().ContainSingle();
        result.Events[0].Source.Should().Be("queue://review/re-review-requests");
    }

    [Fact]
    public void Parse_WithSourceUri_ShouldOverrideSourceInReviewEventFormat()
    {
        // Arrange: ReviewEvent 直接形式（source フィールドあり）
        string json = "{\"eventId\":\"evt_1\",\"repository\":\"org/repo\",\"prNumber\":42,\"prUrl\":\"https://github.com/org/repo/pull/42\",\"reason\":\"re-review-requested\",\"source\":\"thread-owl\",\"message\":\"msg\"}";

        // Act
        ReviewEventParseResult result = ReviewEventParser.Parse(json, "queue://review/re-review-requests");

        // Assert
        result.Events.Should().ContainSingle();
        result.Events[0].Source.Should().Be("queue://review/re-review-requests");
    }

    // キューの 1 件分（候補の配列）。extra は、候補に足す追加の項目（JSON の断片）
    private static string CandidateJson(string? extra, int prNumber = 42)
        => $"[{Candidate(extra, prNumber)}]";

    // 受信形式ごとの JSON。array=候補の配列、single=単一の候補、event=完成済みの ReviewEvent
    private static string PayloadJson(string format, string? extra)
        => format switch
        {
            "array" => CandidateJson(extra),
            "single" => Candidate(extra, 42),
            "event" => $"{{\"eventId\":\"evt_1\",\"repository\":\"org/repo\",\"prNumber\":42,\"prUrl\":\"https://github.com/org/repo/pull/42\",\"reason\":\"opened\",\"source\":\"src\",\"message\":\"msg\"{(extra is null ? string.Empty : "," + extra)}}}",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "未知の受信形式です。"),
        };

    private static string Candidate(string? extra, int prNumber)
        => $"{{\"owner\":\"org\",\"repo\":\"repo\",\"prNumber\":{prNumber},\"installationId\":1,\"queuedAt\":\"2026-10-25T00:00:00.000Z\",\"reason\":\"opened\"{(extra is null ? string.Empty : "," + extra)}}}";
}
