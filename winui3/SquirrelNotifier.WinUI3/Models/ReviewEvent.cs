// <copyright file="ReviewEvent.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Text.Json.Serialization;

namespace SquirrelNotifier.WinUI3.Models;

internal sealed class ReviewEvent
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

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonIgnore]
    public DateTime ReceivedTime { get; set; } = DateTime.Now;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(EventId))
        {
            throw new ArgumentException("EventId cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(PrUrl))
        {
            throw new ArgumentException("PrUrl cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(Repository))
        {
            throw new ArgumentException("Repository cannot be empty.");
        }
    }
}
