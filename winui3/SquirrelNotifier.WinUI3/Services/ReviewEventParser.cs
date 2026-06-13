// <copyright file="ReviewEventParser.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
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
            JsonSerializerOptions options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            };

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                List<ReviewCandidate>? candidates = JsonSerializer.Deserialize<List<ReviewCandidate>>(trimmed, options);
                if (candidates == null || candidates.Count == 0)
                {
                    return null;
                }

                return ConvertToEvent(candidates[0]);
            }
            else if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
            {
                // Try parsing as ReviewEvent first to check if it's already a fully built ReviewEvent
                ReviewEvent? reviewEvent = JsonSerializer.Deserialize<ReviewEvent>(trimmed, options);
                if (reviewEvent != null && !string.IsNullOrEmpty(reviewEvent.Repository) && !string.IsNullOrEmpty(reviewEvent.PrUrl))
                {
                    reviewEvent.Validate();
                    if (!UrlValidator.IsSafeGitHubUrl(reviewEvent.PrUrl, reviewEvent.Repository, reviewEvent.PrNumber))
                    {
                        return null;
                    }

                    return reviewEvent;
                }

                // If not, try parsing as a single ReviewCandidate object
                ReviewCandidate? candidate = JsonSerializer.Deserialize<ReviewCandidate>(trimmed, options);
                if (candidate != null)
                {
                    return ConvertToEvent(candidate);
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static ReviewEvent ConvertToEvent(ReviewCandidate candidate)
    {
        string repository = $"{candidate.Owner}/{candidate.Repo}";
        string prUrl = candidate.Url;

        string eventId = candidate.EventId ?? $"evt_{repository.Replace('/', '_')}_{candidate.PrNumber}_{candidate.Reason}";
        string message = candidate.Message ?? $"{candidate.Reason} by {candidate.Author}";
        string source = !string.IsNullOrEmpty(candidate.Author) ? candidate.Author : "thread-owl";

        ReviewEvent reviewEvent = new ReviewEvent
        {
            EventId = eventId,
            Repository = repository,
            PrNumber = candidate.PrNumber,
            PrUrl = prUrl,
            Reason = candidate.Reason,
            Source = source,
            Message = message,
        };

        reviewEvent.Validate();

        if (!UrlValidator.IsSafeGitHubUrl(reviewEvent.PrUrl, reviewEvent.Repository, reviewEvent.PrNumber))
        {
            throw new ArgumentException("PR URL does not match repository and PR number.");
        }

        return reviewEvent;
    }
}
