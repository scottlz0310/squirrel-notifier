// <copyright file="ProgressEventParserTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class ProgressEventParserTests
{
    [Fact]
    public void TryParse_ShouldParseFullPayload()
    {
        const string line = "@squirrel-progress {\"schemaVersion\":1,\"phaseIndex\":3,\"totalPhases\":8,\"phaseLabel\":\"修正\",\"message\":\"accept 2件を修正中\",\"verdict\":\"APPROVED\",\"timestamp\":\"2026-07-11T10:00:00Z\"}";

        bool parsed = ProgressEventParser.TryParse(line, out AgentProgressEvent? progressEvent);

        parsed.Should().BeTrue();
        progressEvent.Should().NotBeNull();
        progressEvent!.SchemaVersion.Should().Be(1);
        progressEvent.PhaseIndex.Should().Be(3);
        progressEvent.TotalPhases.Should().Be(8);
        progressEvent.PhaseLabel.Should().Be("修正");
        progressEvent.Message.Should().Be("accept 2件を修正中");
        progressEvent.Verdict.Should().Be("APPROVED");
        progressEvent.Timestamp.Should().Be(new DateTimeOffset(2026, 7, 11, 10, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void TryParse_ShouldParseMinimalPayload_WithOptionalFieldsOmitted()
    {
        const string line = "@squirrel-progress {\"schemaVersion\":1,\"phaseIndex\":0,\"totalPhases\":1,\"phaseLabel\":\"実行\"}";

        bool parsed = ProgressEventParser.TryParse(line, out AgentProgressEvent? progressEvent);

        parsed.Should().BeTrue();
        progressEvent!.Message.Should().BeNull();
        progressEvent.Verdict.Should().BeNull();
        progressEvent.Timestamp.Should().BeNull();
    }

    [Fact]
    public void TryParse_ShouldAcceptCaseInsensitivePropertyNames()
    {
        const string line = "@squirrel-progress {\"SchemaVersion\":1,\"PhaseIndex\":0,\"TotalPhases\":2,\"PhaseLabel\":\"sync\"}";

        ProgressEventParser.TryParse(line, out AgentProgressEvent? progressEvent).Should().BeTrue();
        progressEvent!.TotalPhases.Should().Be(2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("通常のログ行です")]
    [InlineData("@squirrel-progress")]
    [InlineData("@squirrel-progress{\"schemaVersion\":1,\"phaseIndex\":0,\"totalPhases\":1,\"phaseLabel\":\"x\"}")]
    [InlineData("@SQUIRREL-PROGRESS {\"schemaVersion\":1,\"phaseIndex\":0,\"totalPhases\":1,\"phaseLabel\":\"x\"}")]
    [InlineData("@squirrel-progress これはJSONではない")]
    [InlineData("@squirrel-progress {\"schemaVersion\":1,\"phaseIndex\":0,")]
    [InlineData("@squirrel-progress [1,2,3]")]
    [InlineData("@squirrel-progress {\"schemaVersion\":2,\"phaseIndex\":0,\"totalPhases\":1,\"phaseLabel\":\"x\"}")]
    [InlineData("@squirrel-progress {\"schemaVersion\":\"1\",\"phaseIndex\":0,\"totalPhases\":1,\"phaseLabel\":\"x\"}")]
    [InlineData("@squirrel-progress {\"phaseIndex\":0,\"totalPhases\":1,\"phaseLabel\":\"x\"}")]
    [InlineData("@squirrel-progress {\"schemaVersion\":1,\"totalPhases\":1,\"phaseLabel\":\"x\"}")]
    [InlineData("@squirrel-progress {\"schemaVersion\":1,\"phaseIndex\":0,\"phaseLabel\":\"x\"}")]
    [InlineData("@squirrel-progress {\"schemaVersion\":1,\"phaseIndex\":0,\"totalPhases\":1}")]
    [InlineData("@squirrel-progress {\"schemaVersion\":1,\"phaseIndex\":-1,\"totalPhases\":1,\"phaseLabel\":\"x\"}")]
    [InlineData("@squirrel-progress {\"schemaVersion\":1,\"phaseIndex\":0,\"totalPhases\":0,\"phaseLabel\":\"x\"}")]
    [InlineData("@squirrel-progress {\"schemaVersion\":1,\"phaseIndex\":0,\"totalPhases\":1,\"phaseLabel\":\"\"}")]
    [InlineData("@squirrel-progress {\"schemaVersion\":1,\"phaseIndex\":0,\"totalPhases\":1,\"phaseLabel\":\"  \"}")]
    public void TryParse_ShouldRejectNonProgressLines(string? line)
    {
        // マーカー不一致・malformed JSON・未知 schemaVersion・必須項目欠落は
        // すべて「通常ログとして扱うべき行」（false）になる（#143 AC）
        bool parsed = ProgressEventParser.TryParse(line, out AgentProgressEvent? progressEvent);

        parsed.Should().BeFalse();
        progressEvent.Should().BeNull();
    }
}
