// <copyright file="LauncherActivityParser.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Globalization;
using System.Text.Json;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Helpers;

internal enum LauncherToolActivityKind
{
    /// <summary>ツールの開始.</summary>
    Started,

    /// <summary>ツールの完了.</summary>
    Completed,
}

internal readonly record struct LauncherToolActivity(LauncherToolActivityKind Kind, string Id);

internal static class LauncherActivityParser
{
    public static IReadOnlyList<LauncherToolActivity> Parse(string line, LauncherOutputFormat format)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            return format switch
            {
                LauncherOutputFormat.CodexJson => ParseCodex(root),
                LauncherOutputFormat.ClaudeStreamJson => ParseClaude(root),
                LauncherOutputFormat.AgyStreamJson => ParseAgy(root),
                LauncherOutputFormat.CopilotJson => ParseCopilot(root),
                _ => [],
            };
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<LauncherToolActivity> ParseCodex(JsonElement root)
    {
        string? type = ReadString(root, "type");
        if ((type != "item.started" && type != "item.completed")
            || !root.TryGetProperty("item", out JsonElement item)
            || item.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        string? itemType = ReadString(item, "type");
        string? id = ReadString(item, "id");
        if (string.IsNullOrEmpty(id) || itemType is not ("command_execution" or "mcp_tool_call" or "collab_tool_call" or "web_search"))
        {
            return [];
        }

        return [new LauncherToolActivity(type == "item.started" ? LauncherToolActivityKind.Started : LauncherToolActivityKind.Completed, id)];
    }

    private static List<LauncherToolActivity> ParseClaude(JsonElement root)
    {
        string? type = ReadString(root, "type");
        if (type is not ("assistant" or "user")
            || !root.TryGetProperty("message", out JsonElement message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out JsonElement content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<LauncherToolActivity>();
        foreach (JsonElement block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? blockType = ReadString(block, "type");
            bool started = type == "assistant" && blockType == "tool_use";
            bool completed = type == "user" && blockType == "tool_result";
            string? id = started ? ReadString(block, "id") : completed ? ReadString(block, "tool_use_id") : null;
            if (!string.IsNullOrEmpty(id))
            {
                result.Add(new LauncherToolActivity(started ? LauncherToolActivityKind.Started : LauncherToolActivityKind.Completed, id));
            }
        }

        return result;
    }

    private static IReadOnlyList<LauncherToolActivity> ParseAgy(JsonElement root)
    {
        if (ReadString(root, "event") != "step_update"
            || !root.TryGetProperty("step_update", out JsonElement step)
            || step.ValueKind != JsonValueKind.Object
            || ReadString(step, "step_type") != "tool"
            || !step.TryGetProperty("step_index", out JsonElement index)
            || index.ValueKind != JsonValueKind.Number
            || !index.TryGetInt64(out long stepIndex))
        {
            return [];
        }

        string? state = ReadString(step, "state");
        if (state is not ("ACTIVE" or "DONE"))
        {
            return [];
        }

        return [new LauncherToolActivity(state == "ACTIVE" ? LauncherToolActivityKind.Started : LauncherToolActivityKind.Completed, stepIndex.ToString(CultureInfo.InvariantCulture))];
    }

    private static IReadOnlyList<LauncherToolActivity> ParseCopilot(JsonElement root)
    {
        string? type = ReadString(root, "type");
        if (type is not ("tool.execution_start" or "tool.execution_complete")
            || !root.TryGetProperty("data", out JsonElement data)
            || data.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        string? id = ReadString(data, "toolCallId");
        return string.IsNullOrEmpty(id)
            ? []
            : [new LauncherToolActivity(type == "tool.execution_start" ? LauncherToolActivityKind.Started : LauncherToolActivityKind.Completed, id)];
    }

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
