// <copyright file="LauncherActivityParserTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public class LauncherActivityParserTests
{
    [Theory]
    [InlineData((int)LauncherOutputFormat.CodexJson, "{\"type\":\"item.started\",\"item\":{\"type\":\"command_execution\",\"id\":\"a\"}}", (int)LauncherToolActivityKind.Started, "a")]
    [InlineData((int)LauncherOutputFormat.CodexJson, "{\"type\":\"item.completed\",\"item\":{\"type\":\"mcp_tool_call\",\"id\":\"b\"}}", (int)LauncherToolActivityKind.Completed, "b")]
    [InlineData((int)LauncherOutputFormat.ClaudeStreamJson, "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"c\"}]}}", (int)LauncherToolActivityKind.Started, "c")]
    [InlineData((int)LauncherOutputFormat.ClaudeStreamJson, "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"c\"}]}}", (int)LauncherToolActivityKind.Completed, "c")]
    [InlineData((int)LauncherOutputFormat.AgyStreamJson, "{\"event\":\"step_update\",\"step_update\":{\"step_type\":\"tool\",\"step_index\":2,\"state\":\"ACTIVE\"}}", (int)LauncherToolActivityKind.Started, "2")]
    [InlineData((int)LauncherOutputFormat.AgyStreamJson, "{\"event\":\"step_update\",\"step_update\":{\"step_type\":\"tool\",\"step_index\":2,\"state\":\"DONE\"}}", (int)LauncherToolActivityKind.Completed, "2")]
    [InlineData((int)LauncherOutputFormat.CopilotJson, "{\"type\":\"tool.execution_start\",\"data\":{\"toolCallId\":\"d\"}}", (int)LauncherToolActivityKind.Started, "d")]
    [InlineData((int)LauncherOutputFormat.CopilotJson, "{\"type\":\"tool.execution_complete\",\"data\":{\"toolCallId\":\"d\"}}", (int)LauncherToolActivityKind.Completed, "d")]
    public void Parse_ShouldExtractToolIdentity(int format, string json, int kind, string id)
    {
        LauncherActivityParser.Parse(json, (LauncherOutputFormat)format).Should().Equal(new LauncherToolActivity((LauncherToolActivityKind)kind, id));
    }

    [Theory]
    [InlineData((int)LauncherOutputFormat.CodexJson, "not json")]
    [InlineData((int)LauncherOutputFormat.CodexJson, "{\"type\":\"item.started\",\"item\":{\"type\":\"reasoning\",\"id\":\"a\"}}")]
    [InlineData((int)LauncherOutputFormat.CodexJson, "{\"type\":\"item.started\",\"item\":{\"type\":\"command_execution\"}}")]
    [InlineData((int)LauncherOutputFormat.AgyStreamJson, "{\"event\":\"step_update\",\"step_update\":{\"step_type\":\"tool\",\"step_index\":2,\"state\":\"UNKNOWN\"}}")]
    public void Parse_ShouldIgnoreUnrecognizedEvents(int format, string json)
    {
        LauncherActivityParser.Parse(json, (LauncherOutputFormat)format).Should().BeEmpty();
    }
}
