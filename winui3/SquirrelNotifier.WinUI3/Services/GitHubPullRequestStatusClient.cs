// <copyright file="GitHubPullRequestStatusClient.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

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

internal sealed class GitHubPullRequestStatusClient : IPullRequestStatusClient, IDisposable
{
    private const string _apiBaseUrl = "https://api.github.com/";
    private static readonly TimeSpan _defaultRequestTimeout = TimeSpan.FromSeconds(5);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _requestTimeout;

    public GitHubPullRequestStatusClient(HttpClient? httpClient = null, TimeSpan? requestTimeout = null)
    {
        TimeSpan timeout = requestTimeout ?? _defaultRequestTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout), "GitHub API のタイムアウトは正の値である必要があります。");
        }

        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _requestTimeout = timeout;
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Squirrel-Notifier-WinUI3", "0.8"));
        }

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
