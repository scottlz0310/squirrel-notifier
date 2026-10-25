// <copyright file="ReviewCandidate.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text.Json;
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
    public long? SourceCommentId { get; set; }

    [JsonPropertyName("requestedBy")]
    public string? RequestedBy { get; set; }

    // review-raven の依頼 cycle との対応。review-raven 経由の候補にだけ付く。形式の検証は ReviewCycleRef.TryRead が行う
    [JsonPropertyName("cycle")]
    public JsonElement? Cycle { get; set; }
}
