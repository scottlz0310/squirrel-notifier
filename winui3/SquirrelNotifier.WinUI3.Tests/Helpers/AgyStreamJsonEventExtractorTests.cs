// <copyright file="AgyStreamJsonEventExtractorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text.Json;
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

    [Theory]
    [InlineData("ACTIVE", "run_command", "Start-Sleep -Seconds 4", null, null, "[tool] run_command: Start-Sleep -Seconds 4")]
    [InlineData("ACTIVE", "run_command", "gh api\n  \"repos/o/r/pulls/1\"\t--jq .number", null, null, "[tool] run_command: gh api \"repos/o/r/pulls/1\" --jq .number")]
    [InlineData("ACTIVE", "view_file", null, null, null, "[tool] view_file")]
    [InlineData("DONE", "run_command", null, "Command exited with code 0.\n\nNo output from the command.", 4.3541175, "[tool] run_command 完了 4.4秒")]
    [InlineData("DONE", "run_command", null, "Command exited with code 1.\n\nCommand output:\n{secret}", 0.6, "[tool] run_command 完了 0.6秒 終了コード 1")]
    [InlineData("DONE", "mcp_call", null, null, null, "[tool] mcp_call 完了")]
    public void TryExtract_ShouldShowToolProgress_WithoutOutputBodyOrNonCommandParameters(
        string state,
        string toolName,
        string? commandLine,
        string? output,
        double? durationSeconds,
        string expected)
    {
        string line = BuildToolStepLine(state, toolName, commandLine, output, durationSeconds);

        bool extracted = AgyStreamJsonEventExtractor.TryExtract(line, out AgyStreamJsonExtraction? result);

        extracted.Should().BeTrue();
        result!.LogLines.Should().Equal(expected);
        result.SessionId.Should().Be(Guid.Parse(_sessionId));
    }

    [Fact]
    public void TryExtract_ShouldNotShowOtherParametersOfNonCommandTools()
    {
        string line = BuildToolStepLine("ACTIVE", "view_file", null, null, null, new Dictionary<string, string> { ["AbsolutePath"] = "C:\\secret.txt" });

        AgyStreamJsonEventExtractor.TryExtract(line, out AgyStreamJsonExtraction? result).Should().BeTrue();

        result!.LogLines.Should().Equal("[tool] view_file");
    }

    [Fact]
    public void TryExtract_ShouldTruncateLongCommandLine()
    {
        string line = BuildToolStepLine("ACTIVE", "run_command", new string('a', 300), null, null);

        AgyStreamJsonEventExtractor.TryExtract(line, out AgyStreamJsonExtraction? result).Should().BeTrue();

        result!.LogLines.Should().Equal($"[tool] run_command: {new string('a', 160)}…");
    }

    [Theory]
    [InlineData("user_input", "DONE", "run_command")]
    [InlineData("tool", "PENDING", "run_command")]
    [InlineData("tool", "ACTIVE", null)]
    public void TryExtract_ShouldNotShowNonToolOrIncompleteToolSteps(string stepType, string state, string? toolName)
    {
        var stepUpdate = new Dictionary<string, object?>
        {
            ["conversation_id"] = _sessionId,
            ["step_index"] = 2,
            ["state"] = state,
            ["step_type"] = stepType,
        };
        if (toolName is not null)
        {
            stepUpdate["tool_name"] = toolName;
        }

        string line = JsonSerializer.Serialize(new Dictionary<string, object?> { ["event"] = "step_update", ["step_update"] = stepUpdate });

        AgyStreamJsonEventExtractor.TryExtract(line, out AgyStreamJsonExtraction? result).Should().BeTrue();

        result!.LogLines.Should().BeEmpty();
    }

    private static string BuildToolStepLine(
        string state,
        string toolName,
        string? commandLine,
        string? output,
        double? durationSeconds,
        Dictionary<string, string>? extraParameters = null)
    {
        var parameters = new Dictionary<string, string>(extraParameters ?? []);
        if (commandLine is not null)
        {
            parameters["CommandLine"] = commandLine;
        }

        var toolInfo = new Dictionary<string, object?> { ["name"] = toolName, ["parameters"] = parameters };
        if (output is not null)
        {
            toolInfo["output"] = output;
        }

        var stepUpdate = new Dictionary<string, object?>
        {
            ["conversation_id"] = _sessionId,
            ["step_index"] = 2,
            ["state"] = state,
            ["step_type"] = "tool",
            ["tool_name"] = toolName,
            ["tool_info"] = toolInfo,
        };
        if (durationSeconds is not null)
        {
            stepUpdate["duration_seconds"] = durationSeconds;
        }

        return JsonSerializer.Serialize(new Dictionary<string, object?> { ["event"] = "step_update", ["step_update"] = stepUpdate });
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
