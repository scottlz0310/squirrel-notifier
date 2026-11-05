// <copyright file="AgyStreamJsonEventExtractor.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// Antigravity CLI の <c>--output-format stream-json</c> が stdout へ出力するイベントから、
/// conversation ID、agent response のテキスト、tool step の進捗を抽出する（#306）.
/// </summary>
internal static partial class AgyStreamJsonEventExtractor
{
    private const int _toolSummaryMaxLength = 160;

    /// <summary>
    /// 1 行を agy stream-json event として解釈する.
    /// </summary>
    /// <param name="line">stdout の 1 行.</param>
    /// <param name="extraction">既知 event の抽出結果.</param>
    /// <returns>既知 event の場合 <see langword="true"/>。それ以外は <see langword="false"/>.</returns>
    public static bool TryExtract(string? line, out AgyStreamJsonExtraction? extraction)
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
                || !TryGetString(root, "event", out string? eventName))
            {
                return false;
            }

            switch (eventName)
            {
                case "init":
                    extraction = ExtractSessionIdFromRoot(root);
                    return true;

                case "step_update":
                    extraction = ExtractStepUpdate(root);
                    return true;

                case "result":
                    extraction = ExtractResult(root);
                    return true;

                default:
                    return false;
            }
        }
    }

    private static AgyStreamJsonExtraction ExtractStepUpdate(JsonElement root)
    {
        if (!root.TryGetProperty("step_update", out JsonElement stepUpdate)
            || stepUpdate.ValueKind != JsonValueKind.Object)
        {
            return AgyStreamJsonExtraction.Empty;
        }

        Guid? sessionId = ExtractSessionId(stepUpdate, out string? failureReason);
        var logLines = new List<string>();
        if (TryGetString(stepUpdate, "step_type", out string? stepType)
            && stepType == "agent_response"
            && TryGetString(stepUpdate, "text_delta", out string? textDelta))
        {
            logLines.AddRange(SplitNonEmptyLines(textDelta!));
        }
        else if (stepType == "tool" && FormatToolProgress(stepUpdate) is string toolLine)
        {
            logLines.Add(toolLine);
        }

        return new AgyStreamJsonExtraction(logLines, sessionId, failureReason);
    }

    /// <summary>
    /// tool step の開始・完了を 1 行にする。agy は本文を持たない turn（tool 呼び出しだけの応答）の間、
    /// 実行ウィンドウに何も出せなくなるため、tool 名と run_command のコマンド行だけを示す.
    /// 出力本文や run_command 以外の引数は、量と機微な内容を避けるため表示しない.
    /// </summary>
    private static string? FormatToolProgress(JsonElement stepUpdate)
    {
        if (!TryGetString(stepUpdate, "tool_name", out string? toolName)
            || !TryGetString(stepUpdate, "state", out string? state))
        {
            return null;
        }

        stepUpdate.TryGetProperty("tool_info", out JsonElement toolInfo);
        if (state == "ACTIVE")
        {
            string? commandLine = toolInfo.ValueKind == JsonValueKind.Object
                && toolInfo.TryGetProperty("parameters", out JsonElement parameters)
                && parameters.ValueKind == JsonValueKind.Object
                && TryGetString(parameters, "CommandLine", out string? command)
                    ? SummarizeForOneLine(command!)
                    : null;
            return string.IsNullOrEmpty(commandLine) ? $"[tool] {toolName}" : $"[tool] {toolName}: {commandLine}";
        }

        if (state != "DONE")
        {
            return null;
        }

        string duration = stepUpdate.TryGetProperty("duration_seconds", out JsonElement seconds)
            && seconds.ValueKind == JsonValueKind.Number
            && seconds.TryGetDouble(out double value)
                ? $" {value.ToString("0.0", CultureInfo.InvariantCulture)}秒"
                : string.Empty;
        string exit = toolInfo.ValueKind == JsonValueKind.Object
            && TryGetString(toolInfo, "output", out string? output)
            && ExitCodePattern().Match(output!) is { Success: true } match
            && match.Groups[1].Value != "0"
                ? $" 終了コード {match.Groups[1].Value}"
                : string.Empty;
        return $"[tool] {toolName} 完了{duration}{exit}";
    }

    private static string SummarizeForOneLine(string text)
    {
        string singleLine = WhitespacePattern().Replace(text, " ").Trim();
        return singleLine.Length <= _toolSummaryMaxLength ? singleLine : singleLine[.._toolSummaryMaxLength] + "…";
    }

    [GeneratedRegex(@"^Command exited with code (-?\d+)")]
    private static partial Regex ExitCodePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    private static AgyStreamJsonExtraction ExtractSessionIdFromRoot(JsonElement root)
    {
        Guid? sessionId = ExtractSessionId(root, out string? failureReason);
        return new AgyStreamJsonExtraction([], sessionId, failureReason);
    }

    private static AgyStreamJsonExtraction ExtractResult(JsonElement root)
    {
        if (!root.TryGetProperty("result", out JsonElement result)
            || result.ValueKind != JsonValueKind.Object)
        {
            return AgyStreamJsonExtraction.Empty;
        }

        Guid? sessionId = ExtractSessionId(result, out string? failureReason);
        var logLines = new List<string>();
        string status = TryGetString(result, "status", out string? statusValue)
            ? statusValue!
            : string.Empty;
        if (!string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetString(result, "error", out string? error))
            {
                logLines.AddRange(SplitNonEmptyLines($"agy result error: {error!}"));
            }

            if (TryGetString(result, "response", out string? response))
            {
                logLines.AddRange(SplitNonEmptyLines(response!));
            }
        }

        return new AgyStreamJsonExtraction(logLines, sessionId, failureReason);
    }

    private static Guid? ExtractSessionId(JsonElement root, out string? failureReason)
    {
        failureReason = null;
        if (!root.TryGetProperty("conversation_id", out JsonElement conversationId))
        {
            return null;
        }

        if (TryGetString(conversationId, out string? value)
            && Guid.TryParseExact(value, "D", out Guid sessionId))
        {
            return sessionId;
        }

        failureReason = "agy の conversation_id が D 形式 UUID ではありません";
        return null;
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
/// <see cref="AgyStreamJsonEventExtractor.TryExtract"/> の抽出結果.
/// </summary>
/// <param name="LogLines">人間向けに表示するテキスト行.</param>
/// <param name="SessionId">抽出できた agy conversation ID.</param>
/// <param name="SessionIdFailureReason">ID 候補が UUID でなかった場合の理由.</param>
internal sealed record AgyStreamJsonExtraction(
    IReadOnlyList<string> LogLines,
    Guid? SessionId,
    string? SessionIdFailureReason)
{
    /// <summary>表示・session ID ともに持たない既知 event 用の空結果.</summary>
    public static readonly AgyStreamJsonExtraction Empty = new([], null, null);
}
