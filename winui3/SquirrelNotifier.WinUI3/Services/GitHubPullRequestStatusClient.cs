// <copyright file="GitHubPullRequestStatusClient.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using SquirrelNotifier.WinUI3.Helpers;

namespace SquirrelNotifier.WinUI3.Services;

internal enum PullRequestLifecycleState
{
    /// <summary>オープン中の PR.</summary>
    Open,

    /// <summary>クローズ済みだがマージされていない PR.</summary>
    Closed,

    /// <summary>マージ済みの PR.</summary>
    Merged,
}

internal interface IPullRequestStatusClient
{
    Task<PullRequestLifecycleState> GetStateAsync(string repository, int prNumber, CancellationToken cancellationToken);
}

/// <summary>
/// GitHub API のレート制限に達し、<see cref="ResetAt"/> まで照会を控えるべきことを表す.
/// </summary>
[SuppressMessage("Design", "CA1032", Justification = "再開時刻を必須とする例外のため、再開時刻を持たない標準コンストラクターは提供しない")]
[SuppressMessage("Roslynator", "RCS1194", Justification = "再開時刻を必須とする例外のため、再開時刻を持たない標準コンストラクターは提供しない")]
internal sealed class GitHubRateLimitException : HttpRequestException
{
    public GitHubRateLimitException(string message, DateTimeOffset resetAt)
        : base(message)
    {
        ResetAt = resetAt;
    }

    public DateTimeOffset ResetAt { get; }
}

internal sealed class GitHubPullRequestStatusClient : IPullRequestStatusClient
{
    private readonly IGhApiClient _ghApiClient;
    private readonly TimeProvider _timeProvider;

    public GitHubPullRequestStatusClient(
        IGhApiClient? ghApiClient = null,
        TimeProvider? timeProvider = null)
    {
        _ghApiClient = ghApiClient ?? new GhApiClient(includeResponseHeaders: true);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<PullRequestLifecycleState> GetStateAsync(
        string repository,
        int prNumber,
        CancellationToken cancellationToken)
    {
        (string owner, string repo) = ParseRepository(repository, prNumber);
        string requestPath = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/pulls/{prNumber}";
        GhApiResult response = await _ghApiClient.GetAsync(
            requestPath,
            "{state, merged_at}",
            paginate: false,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            if (TryGetRateLimitResetTime(response, out DateTimeOffset resetAt))
            {
                throw new GitHubRateLimitException(
                    $"GitHub API のレート制限に達しました: {repository}#{prNumber}。{response.Error}",
                    resetAt);
            }

            throw new HttpRequestException(
                $"GitHub PR 状態の取得に失敗しました: {repository}#{prNumber}。{response.Error}");
        }

        using JsonDocument document = JsonDocument.Parse(response.Output);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("state", out JsonElement stateElement)
            || stateElement.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"GitHub PR status response for {repository}#{prNumber} did not contain a valid state.");
        }

        string state = stateElement.GetString() ?? string.Empty;
        if (state.Equals("open", StringComparison.OrdinalIgnoreCase))
        {
            return PullRequestLifecycleState.Open;
        }

        if (state.Equals("closed", StringComparison.OrdinalIgnoreCase))
        {
            bool isMerged = root.TryGetProperty("merged_at", out JsonElement mergedAtElement)
                && mergedAtElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(mergedAtElement.GetString());
            return isMerged ? PullRequestLifecycleState.Merged : PullRequestLifecycleState.Closed;
        }

        throw new JsonException($"GitHub PR status response for {repository}#{prNumber} contained an unsupported state: {state}.");
    }

    // GitHub の案内に従い retry-after を優先し、無ければ残数 0 のときの x-ratelimit-reset を使う。
    // 再開時刻を決められない 403 / 429 は権限エラー等と区別できないため、通常の失敗として扱う。
    private bool TryGetRateLimitResetTime(GhApiResult response, out DateTimeOffset resetAt)
    {
        resetAt = default;
        if (response.HttpStatus is not (403 or 429) || response.RateLimitHeaders is not GhApiRateLimitHeaders headers)
        {
            return false;
        }

        RetryConditionHeaderValue? retryAfter = RetryConditionHeaderValue.TryParse(headers.RetryAfter, out RetryConditionHeaderValue? parsedRetryAfter)
            ? parsedRetryAfter
            : null;
        if (retryAfter?.Delta is TimeSpan delta)
        {
            resetAt = _timeProvider.GetUtcNow() + delta;
            return true;
        }

        if (retryAfter?.Date is DateTimeOffset date)
        {
            resetAt = date;
            return true;
        }

        if (headers.Remaining == "0"
            && long.TryParse(
                headers.Reset,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long resetEpochSeconds))
        {
            resetAt = DateTimeOffset.FromUnixTimeSeconds(resetEpochSeconds);
            return true;
        }

        return false;
    }

    private static (string Owner, string Repo) ParseRepository(string repository, int prNumber)
    {
        if (prNumber <= 0 || string.IsNullOrWhiteSpace(repository))
        {
            throw new ArgumentException("リポジトリと PR 番号が不正です。", nameof(repository));
        }

        string prUrl = $"https://github.com/{repository}/pull/{prNumber}";
        if (!UrlValidator.IsSafeGitHubUrl(prUrl, repository, prNumber)
            || !PrReferenceParser.TryParse(prUrl, out Models.PrReference? reference)
            || reference is null)
        {
            throw new ArgumentException("GitHub リポジトリ参照が不正です。", nameof(repository));
        }

        return (reference.Owner, reference.Repo);
    }
}
