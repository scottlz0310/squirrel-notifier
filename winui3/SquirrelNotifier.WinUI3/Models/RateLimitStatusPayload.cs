// <copyright file="RateLimitStatusPayload.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SquirrelNotifier.WinUI3.Models;

internal sealed class RateLimitStatusPayload
{
    [JsonPropertyName("limits")]
    public List<RateLimitInfo> Limits { get; set; } = new();
}
