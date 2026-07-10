// <copyright file="ProgressEventParser.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// stdout に混在する構造化 progress event（行頭マーカー付き JSONL）のパーサー（#143）。
/// マーカー不一致・malformed JSON・未対応 schemaVersion の行は progress event として扱わず、
/// 呼び出し側が通常の stdout ログとして処理する.
/// </summary>
internal static class ProgressEventParser
{
    /// <summary>
    /// progress event 行の行頭マーカー（末尾スペース含む完全一致）。
    /// 通常ログとの偶然の衝突は、このマーカーと schemaVersion 検証の二段構えで排除する.
    /// </summary>
    public const string LineMarker = "@squirrel-progress ";

    /// <summary>現在サポートする schemaVersion.</summary>
    public const int SupportedSchemaVersion = 1;

    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// 1 行を progress event として解釈を試みる.
    /// </summary>
    /// <param name="line">stdout の 1 行.</param>
    /// <param name="progressEvent">解釈に成功した場合の progress event.</param>
    /// <returns>有効な progress event の場合 <see langword="true"/>。それ以外（通常ログとして扱うべき行）は <see langword="false"/>.</returns>
    public static bool TryParse(string? line, out AgentProgressEvent? progressEvent)
    {
        progressEvent = null;

        if (line == null || !line.StartsWith(LineMarker, StringComparison.Ordinal))
        {
            return false;
        }

        string payload = line[LineMarker.Length..];

        ProgressEventPayload? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ProgressEventPayload>(payload, _options);
        }
        catch (JsonException)
        {
            return false;
        }

        if (parsed == null
            || parsed.SchemaVersion != SupportedSchemaVersion
            || parsed.PhaseIndex is null or < 0
            || parsed.TotalPhases is null or < 1
            || string.IsNullOrWhiteSpace(parsed.PhaseLabel))
        {
            return false;
        }

        progressEvent = new AgentProgressEvent(
            parsed.SchemaVersion.Value,
            parsed.PhaseIndex.Value,
            parsed.TotalPhases.Value,
            parsed.PhaseLabel,
            parsed.Message,
            parsed.Verdict,
            parsed.Timestamp);
        return true;
    }

    // JSON との対応付け専用の中間 DTO。必須項目の欠落を null で検出するため、
    // すべて nullable にして TryParse 側で検証する.
    private sealed class ProgressEventPayload
    {
        [JsonPropertyName("schemaVersion")]
        public int? SchemaVersion { get; set; }

        [JsonPropertyName("phaseIndex")]
        public int? PhaseIndex { get; set; }

        [JsonPropertyName("totalPhases")]
        public int? TotalPhases { get; set; }

        [JsonPropertyName("phaseLabel")]
        public string? PhaseLabel { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("verdict")]
        public string? Verdict { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTimeOffset? Timestamp { get; set; }
    }
}
