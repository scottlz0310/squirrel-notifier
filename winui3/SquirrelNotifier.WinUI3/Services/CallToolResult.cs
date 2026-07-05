// <copyright file="CallToolResult.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// mcp-resource-subscriber の <c>call --json</c> 出力のスキーマ.
/// </summary>
internal sealed class CallToolResult
{
    [JsonPropertyName("serverUrl")]
    public string? ServerUrl { get; set; }

    [JsonPropertyName("tool")]
    public string? Tool { get; set; }

    [JsonPropertyName("isError")]
    public bool? IsError { get; set; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("content")]
    public List<CallToolContent>? Content { get; set; }

    public string? ExtractText()
    {
        if (Content == null)
        {
            return null;
        }

        foreach (CallToolContent item in Content)
        {
            if (!string.IsNullOrWhiteSpace(item.Text))
            {
                return item.Text;
            }
        }

        return null;
    }
}

internal sealed class CallToolContent
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}
