// <copyright file="NotificationCache.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text.Json.Serialization;

namespace SquirrelNotifier.WinUI3.Models;

internal sealed class NotificationCache
{
    [JsonPropertyName("seenEventIds")]
    public List<string> SeenEventIds { get; set; } = new();

    [JsonPropertyName("recentEvents")]
    public List<CachedReviewEvent> RecentEvents { get; set; } = new();
}

internal sealed class CachedReviewEvent
{
    [JsonPropertyName("eventId")]
    public string EventId { get; set; } = string.Empty;

    [JsonPropertyName("repository")]
    public string Repository { get; set; } = string.Empty;

    [JsonPropertyName("prNumber")]
    public int PrNumber { get; set; }

    [JsonPropertyName("prUrl")]
    public string PrUrl { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("receivedTime")]
    public DateTime ReceivedTime { get; set; }
}
