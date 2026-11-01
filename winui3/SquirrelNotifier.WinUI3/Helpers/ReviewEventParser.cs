// <copyright file="ReviewEventParser.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Text.Json;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Helpers;

internal static class ReviewEventParser
{
    /// <summary>
    /// review event payload を解釈する。空配列（キューに候補が無い正常状態）と
    /// 壊れた payload を <see cref="ReviewEventParseResult.Status"/> で区別する（#230）.
    /// </summary>
    /// <param name="json">thread-owl queue から受け取った payload.</param>
    /// <param name="sourceUri">イベントの取得元 resource URI。指定した場合は payload 内の source を上書きする.</param>
    /// <returns>解釈結果。<see cref="ReviewEventParseStatus.Parsed"/> の場合のみ Events が有効な内容を持つ.</returns>
    public static ReviewEventParseResult Parse(string? json, string? sourceUri = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ReviewEventParseResult.Empty;
        }

        List<ReviewEvent> events = new List<ReviewEvent>();

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
                if (candidates == null)
                {
                    return ReviewEventParseResult.Malformed;
                }

                if (candidates.Count == 0)
                {
                    return ReviewEventParseResult.Empty;
                }

                foreach (ReviewCandidate candidate in candidates)
                {
                    try
                    {
                        ReviewEvent reviewEvent = ConvertToEvent(candidate, sourceUri);
                        events.Add(reviewEvent);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to parse candidate: {ex.Message}");
                    }
                }
            }
            else if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
            {
                // Try parsing as ReviewEvent first to check if it's already a fully built ReviewEvent
                ReviewEvent? reviewEvent = JsonSerializer.Deserialize<ReviewEvent>(trimmed, options);
                if (reviewEvent != null && !string.IsNullOrEmpty(reviewEvent.Repository) && !string.IsNullOrEmpty(reviewEvent.PrUrl))
                {
                    reviewEvent.Validate();
                    if (UrlValidator.IsSafeGitHubUrl(reviewEvent.PrUrl, reviewEvent.Repository, reviewEvent.PrNumber))
                    {
                        if (!string.IsNullOrEmpty(sourceUri))
                        {
                            reviewEvent.Source = sourceUri;
                        }

                        events.Add(reviewEvent);
                        return ReviewEventParseResult.Parsed(events);
                    }
                }

                // If not, try parsing as a single ReviewCandidate object
                ReviewCandidate? candidate = JsonSerializer.Deserialize<ReviewCandidate>(trimmed, options);
                if (candidate != null)
                {
                    try
                    {
                        ReviewEvent converted = ConvertToEvent(candidate, sourceUri);
                        events.Add(converted);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to parse candidate: {ex.Message}");
                    }
                }
            }
        }
        catch
        {
            return ReviewEventParseResult.Malformed;
        }

        // ここに到達して 0 件なのは「JSON ではない」「想定スキーマから 1 件も構築できなかった」
        // のいずれか。空配列（正常な空キュー）は上で早期 return 済み
        return events.Count == 0
            ? ReviewEventParseResult.Malformed
            : ReviewEventParseResult.Parsed(events);
    }

    private static ReviewEvent ConvertToEvent(ReviewCandidate candidate, string? sourceUri = null)
    {
        if (string.IsNullOrEmpty(candidate.Owner) || string.IsNullOrEmpty(candidate.Repo))
        {
            throw new ArgumentException("Owner and Repo cannot be empty.");
        }

        string repository = $"{candidate.Owner}/{candidate.Repo}";
        string prUrl = $"https://github.com/{candidate.Owner}/{candidate.Repo}/pull/{candidate.PrNumber}";

        string queuedAtStr = !string.IsNullOrEmpty(candidate.QueuedAt) ? candidate.QueuedAt : "unknown_time";
        string safeOwner = candidate.Owner.Replace('/', '_').Replace('\\', '_').Replace(' ', '_');
        string safeRepo = candidate.Repo.Replace('/', '_').Replace('\\', '_').Replace(' ', '_');
        string safeReason = candidate.Reason.Replace('/', '_').Replace('\\', '_').Replace(' ', '_');
        string safeQueuedAt = queuedAtStr.Replace('/', '_').Replace('\\', '_').Replace(':', '_').Replace(' ', '_');

        string eventId = $"evt_{safeOwner}_{safeRepo}_{candidate.PrNumber}_{safeReason}_{safeQueuedAt}";

        string source = !string.IsNullOrEmpty(sourceUri) ? sourceUri
            : !string.IsNullOrEmpty(candidate.RequestedBy) ? candidate.RequestedBy : "thread-owl";
        string message = !string.IsNullOrEmpty(candidate.RequestedBy)
            ? $"{candidate.Reason} by {candidate.RequestedBy}"
            : candidate.Reason;

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
