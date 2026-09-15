// <copyright file="GitHubPullRequestStatusClient.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
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

internal sealed class GitHubPullRequestStatusClient : IPullRequestStatusClient, IDisposable
{
    private const string _apiBaseUrl = "https://api.github.com/";
    private static readonly TimeSpan _defaultRequestTimeout = TimeSpan.FromSeconds(5);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeProvider _timeProvider;

    public GitHubPullRequestStatusClient(
        HttpClient? httpClient = null,
        TimeSpan? requestTimeout = null,
        TimeProvider? timeProvider = null)
    {
        TimeSpan timeout = requestTimeout ?? _defaultRequestTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout), "GitHub API のタイムアウトは正の値である必要があります。");
        }

        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _requestTimeout = timeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
        ApplicationUserAgent.AddDefaultIfMissing(_httpClient);

        if (_httpClient.DefaultRequestHeaders.Accept.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        }
    }

    public async Task<PullRequestLifecycleState> GetStateAsync(
        string repository,
        int prNumber,
        CancellationToken cancellationToken)
    {
        (string owner, string repo) = ParseRepository(repository, prNumber);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_requestTimeout);

        string requestPath = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/pulls/{prNumber}";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(_apiBaseUrl), requestPath));
        using HttpResponseMessage response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (TryGetRateLimitResetTime(response, out DateTimeOffset resetAt))
            {
                throw new GitHubRateLimitException(
                    $"GitHub API rate limit exceeded for {repository}#{prNumber}: HTTP {(int)response.StatusCode} ({response.StatusCode}).",
                    resetAt);
            }

            throw new HttpRequestException(
                $"GitHub PR status request failed for {repository}#{prNumber}: HTTP {(int)response.StatusCode} ({response.StatusCode}).");
        }

        string content = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(content);
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

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    // GitHub の案内に従い retry-after を優先し、無ければ残数 0 のときの x-ratelimit-reset を使う。
    // 再開時刻を決められない 403 / 429 は権限エラー等と区別できないため、通常の失敗として扱う。
    private bool TryGetRateLimitResetTime(HttpResponseMessage response, out DateTimeOffset resetAt)
    {
        resetAt = default;
        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests))
        {
            return false;
        }

        RetryConditionHeaderValue? retryAfter = response.Headers.RetryAfter;
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

        if (GetSingleHeaderValue(response, "x-ratelimit-remaining") == "0"
            && long.TryParse(
                GetSingleHeaderValue(response, "x-ratelimit-reset"),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long resetEpochSeconds))
        {
            resetAt = DateTimeOffset.FromUnixTimeSeconds(resetEpochSeconds);
            return true;
        }

        return false;
    }

    private static string? GetSingleHeaderValue(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out IEnumerable<string>? values) ? values.FirstOrDefault() : null;

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
