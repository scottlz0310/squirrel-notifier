// <copyright file="CodexJsonEventExtractorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public sealed class CodexJsonEventExtractorTests
{
    private const string _sessionId = "01234567-89ab-cdef-0123-456789abcdef";

    [Fact]
    public void TryExtract_ShouldExtractThreadId()
    {
        bool extracted = CodexJsonEventExtractor.TryExtract(
            $"{{\"type\":\"thread.started\",\"thread_id\":\"{_sessionId}\"}}",
            out CodexJsonExtraction? result);

        extracted.Should().BeTrue();
        result!.SessionId.Should().Be(Guid.Parse(_sessionId));
        result.LogLines.Should().BeEmpty();
        result.SessionIdFailureReason.Should().BeNull();
    }

    [Fact]
    public void TryExtract_ShouldPublishAgentMessageText_AndSuppressProtocolEvents()
    {
        const string agentMessage = """{"type":"item.completed","item":{"id":"item_1","type":"agent_message","text":"レビューを開始します。\n差分を確認します。"}}""";
        const string turnCompleted = """{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":2}}""";

        CodexJsonEventExtractor.TryExtract(agentMessage, out CodexJsonExtraction? messageResult).Should().BeTrue();
        CodexJsonEventExtractor.TryExtract(turnCompleted, out CodexJsonExtraction? completedResult).Should().BeTrue();

        messageResult!.LogLines.Should().Equal("レビューを開始します。", "差分を確認します。");
        completedResult!.LogLines.Should().BeEmpty();
    }

    [Fact]
    public void TryExtract_ShouldKeepInvalidThreadIdAsFailureReason()
    {
        bool extracted = CodexJsonEventExtractor.TryExtract(
            "{\"type\":\"thread.started\",\"thread_id\":\"not-a-uuid\"}",
            out CodexJsonExtraction? result);

        extracted.Should().BeTrue();
        result!.SessionId.Should().BeNull();
        result.SessionIdFailureReason.Should().Contain("D 形式 UUID");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("通常のログ行です")]
    [InlineData("{broken json")]
    [InlineData("{\"type\":\"unknown.future\"}")]
    [InlineData("{\"type\":\"item.completed\",\"item\":{\"type\":\"future_item\"}}")]
    public void TryExtract_ShouldRejectUnknownOrMalformedLines(string? line)
    {
        bool extracted = CodexJsonEventExtractor.TryExtract(line, out CodexJsonExtraction? result);

        extracted.Should().BeFalse();
        result.Should().BeNull();
    }
}
