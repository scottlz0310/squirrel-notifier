// <copyright file="SubscriptionResult.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text.Json.Serialization;

namespace SquirrelNotifier.WinUI3.Services;

internal sealed class SubscriptionResult
{
    [JsonPropertyName("route")]
    public string? Route { get; set; }

    [JsonPropertyName("serverUrl")]
    public string? ServerUrl { get; set; }

    [JsonPropertyName("resourceUri")]
    public string? ResourceUri { get; set; }

    [JsonPropertyName("subscribed")]
    public bool? Subscribed { get; set; }

    [JsonPropertyName("notificationReceived")]
    public bool? NotificationReceived { get; set; }

    [JsonPropertyName("notificationCount")]
    public int? NotificationCount { get; set; }

    [JsonPropertyName("unsubscribed")]
    public bool? Unsubscribed { get; set; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("initialText")]
    public string? InitialText { get; set; }

    [JsonPropertyName("finalText")]
    public string? FinalText { get; set; }

    [JsonPropertyName("recommendedNextAction")]
    public string? RecommendedNextAction { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Route))
        {
            throw new System.Text.Json.JsonException("Required property 'route' is missing or empty.");
        }
    }
}
