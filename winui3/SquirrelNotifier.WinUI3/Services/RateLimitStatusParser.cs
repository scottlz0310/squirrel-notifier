// <copyright file="RateLimitStatusParser.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Text.Json;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

internal static class RateLimitStatusParser
{
    // ratelimit:// URI は McpSubscriptionService の常時購読（ReviewEventParser 前提）とは
    // 別経路（手動 resources/read）で読み取るため、両サービスで同じスキーム判定を共有する。
    public const string UriScheme = "ratelimit://";

    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static List<RateLimitInfo> Parse(string? json, string? sourceUri = null)
    {
        List<RateLimitInfo> limits = new();
        RateLimitStatusPayload? payload = TryDeserialize(json);
        if (payload == null)
        {
            return limits;
        }

        foreach (RateLimitInfo info in payload.Limits ?? [])
        {
            try
            {
                info.Validate();
                info.SourceUri = sourceUri ?? string.Empty;
                limits.Add(info);
            }
            catch (ArgumentException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to parse rate limit entry: {ex.Message}");
            }
        }

        return limits;
    }

    /// <summary>
    /// 新スキーマ（schemaVersion / agentId / observedAt 付き）の payload を
    /// <see cref="RateLimitSnapshot"/> として解釈する（#145）。旧スキーマ（resetAt のみ）の
    /// payload や malformed JSON では <see langword="null"/> を返す（使用率・Delta 判定の対象外）.
    /// </summary>
    /// <param name="json">statusline / hook スクリプトが書き出した JSON.</param>
    /// <returns>解析できた場合はスナップショット。それ以外は <see langword="null"/>.</returns>
    public static RateLimitSnapshot? ParseSnapshot(string? json)
    {
        RateLimitStatusPayload? payload = TryDeserialize(json);
        if (payload == null || !payload.IsUsageCapable)
        {
            return null;
        }

        List<RateLimitInfo> limits = new();
        foreach (RateLimitInfo info in payload.Limits ?? [])
        {
            try
            {
                info.Validate();
                limits.Add(info);
            }
            catch (ArgumentException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to parse rate limit entry: {ex.Message}");
            }
        }

        return new RateLimitSnapshot(payload.AgentId!, payload.ObservedAt!.Value, limits);
    }

    private static RateLimitStatusPayload? TryDeserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RateLimitStatusPayload>(json.Trim(), _options);
        }
        catch (JsonException)
        {
            // 不正なJSONは呼び出し側で「取得不可」として扱う
            return null;
        }
    }
}
