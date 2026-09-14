// <copyright file="CodexJsonEventExtractor.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// Codex CLI の <c>exec --json</c> が stdout へ出力する JSONL イベントから、
/// session ID とライブログへ表示する agent message を抽出する（#306）。
/// 未知イベントや未知の item type は生の行へフォールバックできるよう、抽出対象外として返す.
/// </summary>
internal static class CodexJsonEventExtractor
{
    /// <summary>
    /// 1 行を Codex JSON event として解釈する.
    /// </summary>
    /// <param name="line">stdout の 1 行.</param>
    /// <param name="extraction">既知 event の抽出結果.</param>
    /// <returns>既知 event の場合 <see langword="true"/>。それ以外は <see langword="false"/>.</returns>
    public static bool TryExtract(string? line, out CodexJsonExtraction? extraction)
    {
        extraction = null;

        if (line == null || !line.TrimStart().StartsWith('{'))
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetString(root, "type", out string? type))
            {
                return false;
            }

            switch (type)
            {
                case "thread.started":
                    extraction = ExtractThreadStarted(root);
                    return true;

                case "turn.started":
                case "turn.completed":
                case "item.started":
                case "item.updated":
                    extraction = CodexJsonExtraction.Empty;
                    return true;

                case "turn.failed":
                    extraction = ExtractNestedError(root, "codex turn failed");
                    return true;

                case "error":
                    extraction = ExtractError(root, "codex JSON error");
                    return true;

                case "item.completed":
                    return TryExtractCompletedItem(root, out extraction);

                default:
                    return false;
            }
        }
    }

    private static CodexJsonExtraction ExtractThreadStarted(JsonElement root)
    {
        if (!root.TryGetProperty("thread_id", out JsonElement threadIdElement))
        {
            return CodexJsonExtraction.Empty;
        }

        if (TryGetString(threadIdElement, out string? threadId)
            && Guid.TryParseExact(threadId, "D", out Guid sessionId))
        {
            return new CodexJsonExtraction([], sessionId, null);
        }

        return new CodexJsonExtraction(
            [],
            null,
            "thread.started の thread_id が D 形式 UUID ではありません");
    }

    private static bool TryExtractCompletedItem(JsonElement root, out CodexJsonExtraction? extraction)
    {
        extraction = null;
        if (!root.TryGetProperty("item", out JsonElement item)
            || item.ValueKind != JsonValueKind.Object
            || !TryGetString(item, "type", out string? itemType))
        {
            return false;
        }

        switch (itemType)
        {
            case "agent_message":
                extraction = ExtractText(item, "text");
                return true;

            case "error":
                extraction = ExtractError(item, "codex item error");
                return true;

            case "reasoning":
            case "command_execution":
            case "file_change":
            case "mcp_tool_call":
            case "collab_tool_call":
            case "web_search":
            case "todo_list":
                extraction = CodexJsonExtraction.Empty;
                return true;

            default:
                return false;
        }
    }

    private static CodexJsonExtraction ExtractNestedError(JsonElement root, string prefix)
    {
        if (root.TryGetProperty("error", out JsonElement error)
            && error.ValueKind == JsonValueKind.Object)
        {
            return ExtractError(error, prefix);
        }

        return CodexJsonExtraction.Empty;
    }

    private static CodexJsonExtraction ExtractError(JsonElement root, string prefix)
    {
        return TryGetString(root, "message", out string? message)
            ? new CodexJsonExtraction([.. SplitNonEmptyLines($"{prefix}: {message!}")], null, null)
            : CodexJsonExtraction.Empty;
    }

    private static CodexJsonExtraction ExtractText(JsonElement root, string propertyName)
    {
        return TryGetString(root, propertyName, out string? text)
            ? new CodexJsonExtraction([.. SplitNonEmptyLines(text!)], null, null)
            : CodexJsonExtraction.Empty;
    }

    private static IEnumerable<string> SplitNonEmptyLines(string text)
    {
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line;
            }
        }
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string? value)
    {
        value = null;
        return root.TryGetProperty(propertyName, out JsonElement element)
            && TryGetString(element, out value);
    }

    private static bool TryGetString(JsonElement element, out string? value)
    {
        value = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        return value is not null;
    }
}

/// <summary>
/// <see cref="CodexJsonEventExtractor.TryExtract"/> の抽出結果.
/// </summary>
/// <param name="LogLines">人間向けに表示するテキスト行.</param>
/// <param name="SessionId">抽出できた Codex thread ID.</param>
/// <param name="SessionIdFailureReason">ID 候補が UUID でなかった場合の理由.</param>
internal sealed record CodexJsonExtraction(
    IReadOnlyList<string> LogLines,
    Guid? SessionId,
    string? SessionIdFailureReason)
{
    /// <summary>表示・session ID ともに持たない既知 event 用の空結果.</summary>
    public static readonly CodexJsonExtraction Empty = new([], null, null);
}
