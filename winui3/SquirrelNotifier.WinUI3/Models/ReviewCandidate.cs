// <copyright file="ReviewCandidate.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text.Json.Serialization;

namespace SquirrelNotifier.WinUI3.Models;

internal sealed class ReviewCandidate
{
    [JsonPropertyName("owner")]
    public string Owner { get; set; } = string.Empty;

    [JsonPropertyName("repo")]
    public string Repo { get; set; } = string.Empty;

    [JsonPropertyName("prNumber")]
    public int PrNumber { get; set; }

    [JsonPropertyName("installationId")]
    public int InstallationId { get; set; }

    [JsonPropertyName("queuedAt")]
    public string QueuedAt { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("sourceCommentId")]
    public string? SourceCommentId { get; set; }

    [JsonPropertyName("requestedBy")]
    public string? RequestedBy { get; set; }
}
