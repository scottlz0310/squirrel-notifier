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

    /// <summary>
    /// json が旧形式（<c>schemaVersion</c> / <c>agentId</c> / <c>observedAt</c> のいずれかを欠く
    /// resetAt-only）の payload かどうかを判定する（#168）。旧形式は <see cref="Parse"/> で一覧表示は
    /// できるが <see cref="ParseSnapshot"/> は常に <see langword="null"/> を返すため、Auto-Pause
    /// gate（#147）の判定対象から silent に外れる。UI 側の警告表示に使う.
    /// </summary>
    /// <param name="json">statusline / hook スクリプトが書き出した JSON.</param>
    /// <returns>limits を含む有効な JSON だが新スキーマの必須フィールドを欠く場合は <see langword="true"/>.</returns>
    public static bool IsLegacySchema(string? json)
    {
        RateLimitStatusPayload? payload = TryDeserialize(json);
        return payload != null && payload.Limits is { Count: > 0 } && !payload.IsUsageCapable;
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
