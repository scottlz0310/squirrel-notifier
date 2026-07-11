// <copyright file="RateLimitStatusPayload.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SquirrelNotifier.WinUI3.Models;

internal sealed class RateLimitStatusPayload
{
    // schemaVersion / agentId / observedAt は #145 で追加された新スキーマのフィールド。
    // 旧スキーマ（resetAt のみ）の payload にはこれらが存在せず null になる。この場合は
    // 通知予約用途としてのみ扱い、使用率・Delta・freshness 判定の対象外とする（呼び出し側の責務）.
    [JsonPropertyName("schemaVersion")]
    public int? SchemaVersion { get; set; }

    [JsonPropertyName("agentId")]
    public string? AgentId { get; set; }

    [JsonPropertyName("observedAt")]
    public DateTimeOffset? ObservedAt { get; set; }

    [JsonPropertyName("limits")]
    public List<RateLimitInfo> Limits { get; set; } = new();

    // 使用率・Delta・freshness 判定に使える新スキーマ payload かどうか.
    [JsonIgnore]
    public bool IsUsageCapable => SchemaVersion.HasValue && !string.IsNullOrWhiteSpace(AgentId) && ObservedAt.HasValue;
}
