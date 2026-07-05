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

    public static List<RateLimitInfo> Parse(string? json, string? sourceUri = null)
    {
        List<RateLimitInfo> limits = new();
        if (string.IsNullOrWhiteSpace(json))
        {
            return limits;
        }

        try
        {
            JsonSerializerOptions options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            };

            RateLimitStatusPayload? payload = JsonSerializer.Deserialize<RateLimitStatusPayload>(json.Trim(), options);
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
        }
        catch (JsonException)
        {
            // 不正なJSONは空リストとして扱う
        }

        return limits;
    }
}
