// <copyright file="CopilotJsonEventExtractorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class CopilotJsonEventExtractorTests
{
    [Theory]
    [InlineData("{\"type\":\"tool.execution_start\",\"data\":{\"arguments\":\"secret\"}}")]
    [InlineData("{\"type\":\"user.message\",\"data\":{\"content\":\"secret\"}}")]
    [InlineData("invalid json")]
    public void Extract_ShouldNotDisplayOtherEvents(string line)
    {
        new CopilotJsonEventExtractor().Extract(line).Should().BeNull();
    }

    [Fact]
    public void Extract_ShouldAvoidRepeatingStreamedMessage()
    {
        var extractor = new CopilotJsonEventExtractor();
        extractor.Extract("{\"type\":\"assistant.message_delta\",\"data\":{\"messageId\":\"one\",\"deltaContent\":\"hello\"}}").Should().Be("hello");
        extractor.Extract("{\"type\":\"assistant.message\",\"data\":{\"messageId\":\"one\",\"content\":\"hello\"}}").Should().BeNull();
        extractor.Extract("{\"type\":\"assistant.message\",\"data\":{\"messageId\":\"two\",\"content\":\"final\"}}").Should().Be("final");
    }
}
