// <copyright file="ClaudeStreamJsonEventExtractor.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Text.Json;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// Claude Code CLI の <c>--output-format stream-json</c> が stdout へ出力する JSONL イベントから、
/// progress marker（<see cref="ProgressEventParser.LineMarker"/>）と通常ログとして表示すべき
/// テキストを抽出する（#187）。
/// stream-json では、スキルが Bash ツールで echo したマーカーは <c>user</c> イベント内の
/// <c>tool_result</c> テキストに埋め込まれて届くため、行頭マーカーだけを見る
/// <see cref="ProgressEventParser"/> 単体では実行中に取得できない。
/// 既知イベントとして解釈できない行（malformed JSON・未知 type 等）は抽出対象外（false）とし、
/// 呼び出し側が通常の stdout ログとして処理する（レビュー実行自体は失敗させない）.
/// </summary>
internal static class ClaudeStreamJsonEventExtractor
{
    /// <summary>
    /// 1 行を Claude stream-json イベントとして解釈を試みる.
    /// </summary>
    /// <param name="line">stdout の 1 行.</param>
    /// <param name="extraction">解釈に成功した場合の抽出結果（抽出対象が無い既知イベントは空）.</param>
    /// <returns>既知の stream-json イベントの場合 <see langword="true"/>。それ以外（通常ログとして扱うべき行）は <see langword="false"/>.</returns>
    public static bool TryExtract(string? line, out ClaudeStreamJsonExtraction? extraction)
    {
        extraction = null;

        if (line == null || !line.StartsWith('{'))
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
                || !root.TryGetProperty("type", out JsonElement typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            switch (typeElement.GetString())
            {
                // 実行メタデータ（init 等）と partial message の差分イベントは表示対象を持たない
                case "system":
                case "stream_event":
                    extraction = ClaudeStreamJsonExtraction.Empty;
                    return true;

                case "assistant":
                    extraction = ExtractAssistantText(root);
                    return true;

                case "user":
                    extraction = ExtractToolResultMarkers(root);
                    return true;

                case "result":
                    extraction = ExtractResult(root);
                    return true;

                default:
                    // 未知 type は互換性のため通常ログとして生の行を表示する（#187 AC）
                    return false;
            }
        }
    }

    // assistant イベントの text ブロック（実行中のナレーション・最終応答）を通常ログとして抽出する。
    // tool_use ブロックは入力引数（スキル呼び出しの echo コマンド等）でありログには流さない.
    private static ClaudeStreamJsonExtraction ExtractAssistantText(JsonElement root)
    {
        var logLines = new List<string>();
        foreach (JsonElement block in EnumerateMessageContent(root))
        {
            if (GetStringProperty(block, "type") == "text"
                && GetStringProperty(block, "text") is string text)
            {
                AppendNonEmptyLines(logLines, text);
            }
        }

        return new ClaudeStreamJsonExtraction([], logLines);
    }

    // user イベントの tool_result テキストからは progress marker 行のみを抽出する。
    // マーカー以外のツール出力（Read のファイル全文等）は数千行になりうるため、
    // ライブログには流さず抑制する（#187 の設計判断）.
    private static ClaudeStreamJsonExtraction ExtractToolResultMarkers(JsonElement root)
    {
        var progressEvents = new List<AgentProgressEvent>();
        foreach (JsonElement block in EnumerateMessageContent(root))
        {
            if (GetStringProperty(block, "type") != "tool_result"
                || !block.TryGetProperty("content", out JsonElement content))
            {
                continue;
            }

            foreach (string text in EnumerateToolResultTexts(content))
            {
                foreach (string candidate in text.Split('\n'))
                {
                    if (ProgressEventParser.TryParse(candidate.TrimEnd('\r'), out AgentProgressEvent? progressEvent))
                    {
                        progressEvents.Add(progressEvent!);
                    }
                }
            }
        }

        return new ClaudeStreamJsonExtraction(progressEvents, []);
    }

    // result イベントの最終応答テキストは assistant イベントで既に配信済みのため成功時は抑制する。
    // エラー終了（error_max_turns 等）は assistant 側に対応するテキストが無いことがあるため表示する.
    private static ClaudeStreamJsonExtraction ExtractResult(JsonElement root)
    {
        bool isError = root.TryGetProperty("is_error", out JsonElement isErrorElement)
            && isErrorElement.ValueKind == JsonValueKind.True;
        if (!isError)
        {
            return ClaudeStreamJsonExtraction.Empty;
        }

        var logLines = new List<string>();
        if (GetStringProperty(root, "result") is string result)
        {
            AppendNonEmptyLines(logLines, result);
        }

        if (logLines.Count == 0 && GetStringProperty(root, "subtype") is string subtype)
        {
            logLines.Add($"claude stream-json result: {subtype}");
        }

        return new ClaudeStreamJsonExtraction([], logLines);
    }

    // message.content が配列の場合の content block を列挙する（string 形式の content は
    // tool_result / text block を含まないため対象外）
    private static IEnumerable<JsonElement> EnumerateMessageContent(JsonElement root)
    {
        if (!root.TryGetProperty("message", out JsonElement message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out JsonElement content)
            || content.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (JsonElement block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object)
            {
                yield return block;
            }
        }
    }

    // tool_result の content は string と content block 配列の両形式がある
    private static IEnumerable<string> EnumerateToolResultTexts(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            if (content.GetString() is string text)
            {
                yield return text;
            }

            yield break;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (JsonElement block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object
                && GetStringProperty(block, "type") == "text"
                && GetStringProperty(block, "text") is string blockText)
            {
                yield return blockText;
            }
        }
    }

    private static void AppendNonEmptyLines(List<string> logLines, string text)
    {
        foreach (string rawLine in text.Split('\n'))
        {
            string logLine = rawLine.TrimEnd('\r');
            if (!string.IsNullOrWhiteSpace(logLine))
            {
                logLines.Add(logLine);
            }
        }
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}

/// <summary>
/// <see cref="ClaudeStreamJsonEventExtractor.TryExtract"/> の抽出結果。
/// 1 イベントから複数の progress event / ログ行が抽出されうる.
/// </summary>
/// <param name="ProgressEvents">tool_result から抽出された progress event（出現順）.</param>
/// <param name="LogLines">通常ログとして配信すべきテキスト行（出現順）.</param>
internal sealed record ClaudeStreamJsonExtraction(
    IReadOnlyList<AgentProgressEvent> ProgressEvents,
    IReadOnlyList<string> LogLines)
{
    /// <summary>表示対象を持たない既知イベント用の空の抽出結果.</summary>
    public static readonly ClaudeStreamJsonExtraction Empty = new([], []);
}
