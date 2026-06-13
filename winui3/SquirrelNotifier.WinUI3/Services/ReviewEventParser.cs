// <copyright file="ReviewEventParser.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Text.Json;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

internal static class ReviewEventParser
{
    public static ReviewEvent? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            string trimmed = json.Trim();
            if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}'))
            {
                return null;
            }

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            };

            ReviewEvent? reviewEvent = JsonSerializer.Deserialize<ReviewEvent>(trimmed, options);
            if (reviewEvent == null)
            {
                return null;
            }

            reviewEvent.Validate();

            // Validate URL security
            if (!UrlValidator.IsSafeGitHubUrl(reviewEvent.PrUrl))
            {
                return null;
            }

            return reviewEvent;
        }
        catch
        {
            return null;
        }
    }
}
