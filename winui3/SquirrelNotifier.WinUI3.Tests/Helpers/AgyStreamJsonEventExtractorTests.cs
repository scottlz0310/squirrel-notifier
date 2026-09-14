// <copyright file="AgyStreamJsonEventExtractorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public sealed class AgyStreamJsonEventExtractorTests
{
    private const string _sessionId = "01234567-89ab-cdef-0123-456789abcdef";

    [Fact]
    public void TryExtract_ShouldExtractConversationIdFromInitEvent()
    {
        const string line = "{\"event\":\"init\",\"conversation_id\":\"01234567-89ab-cdef-0123-456789abcdef\",\"init\":{}}";

        bool extracted = AgyStreamJsonEventExtractor.TryExtract(line, out AgyStreamJsonExtraction? result);

        extracted.Should().BeTrue();
        result!.SessionId.Should().Be(Guid.Parse(_sessionId));
        result.LogLines.Should().BeEmpty();
    }

    [Fact]
    public void TryExtract_ShouldExtractConversationIdAndTextDelta()
    {
        const string line = """{"event":"step_update","step_update":{"conversation_id":"01234567-89ab-cdef-0123-456789abcdef","step_index":2,"state":"ACTIVE","step_type":"agent_response","text_delta":"レビューを開始します。"}}""";

        bool extracted = AgyStreamJsonEventExtractor.TryExtract(line, out AgyStreamJsonExtraction? result);

        extracted.Should().BeTrue();
        result!.SessionId.Should().Be(Guid.Parse(_sessionId));
        result.LogLines.Should().Equal("レビューを開始します。");
        result.SessionIdFailureReason.Should().BeNull();
    }

    [Fact]
    public void TryExtract_ShouldShowErrorResult_AndSuppressSuccessfulResponseEnvelope()
    {
        const string success = """{"event":"result","result":{"conversation_id":"01234567-89ab-cdef-0123-456789abcdef","status":"SUCCESS","response":"成功応答"}}""";
        const string failure = """{"event":"result","result":{"conversation_id":"01234567-89ab-cdef-0123-456789abcdef","status":"ERROR","response":"失敗応答","error":"認証に失敗しました"}}""";

        AgyStreamJsonEventExtractor.TryExtract(success, out AgyStreamJsonExtraction? successResult).Should().BeTrue();
        AgyStreamJsonEventExtractor.TryExtract(failure, out AgyStreamJsonExtraction? failureResult).Should().BeTrue();

        successResult!.LogLines.Should().BeEmpty();
        failureResult!.LogLines.Should().Equal("agy result error: 認証に失敗しました", "失敗応答");
    }

    [Fact]
    public void TryExtract_ShouldKeepInvalidConversationIdAsFailureReason()
    {
        bool extracted = AgyStreamJsonEventExtractor.TryExtract(
            "{\"event\":\"result\",\"result\":{\"conversation_id\":\"not-a-uuid\",\"status\":\"ERROR\"}}",
            out AgyStreamJsonExtraction? result);

        extracted.Should().BeTrue();
        result!.SessionId.Should().BeNull();
        result.SessionIdFailureReason.Should().Contain("D 形式 UUID");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("通常のログ行です")]
    [InlineData("{broken json")]
    [InlineData("{\"event\":\"future_event\"}")]
    public void TryExtract_ShouldRejectUnknownOrMalformedLines(string? line)
    {
        bool extracted = AgyStreamJsonEventExtractor.TryExtract(line, out AgyStreamJsonExtraction? result);

        extracted.Should().BeFalse();
        result.Should().BeNull();
    }
}
