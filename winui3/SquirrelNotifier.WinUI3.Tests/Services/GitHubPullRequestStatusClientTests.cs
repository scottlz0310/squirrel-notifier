// <copyright file="GitHubPullRequestStatusClientTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class GitHubPullRequestStatusClientTests
{
    private static readonly DateTimeOffset _now = new(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetStateAsync_ShouldReturnOpenAndBuildSafeApiRequest()
    {
        using var handler = new RecordingHandler(_ => JsonResponse("{\"state\":\"open\",\"merged_at\":null}"));
        using var httpClient = new HttpClient(handler);
        using var client = new GitHubPullRequestStatusClient(httpClient);

        PullRequestLifecycleState result = await client.GetStateAsync("owner/repo", 42, CancellationToken.None);

        result.Should().Be(PullRequestLifecycleState.Open);
        handler.Request!.RequestUri!.ToString().Should().Be("https://api.github.com/repos/owner/repo/pulls/42");
        handler.Request.Headers.UserAgent.Should().ContainSingle(value => value.Product != null && value.Product.Name == "Squirrel-Notifier-WinUI3");
        handler.Request.Headers.Accept.Should().ContainSingle(value => value.MediaType == "application/vnd.github+json");
    }

    [Fact]
    public async Task GetStateAsync_ShouldDistinguishClosedAndMerged()
    {
        using GitHubPullRequestStatusClient closedClient = CreateClient("{\"state\":\"closed\",\"merged_at\":null}");
        using GitHubPullRequestStatusClient mergedClient = CreateClient("{\"state\":\"closed\",\"merged_at\":\"2026-09-11T00:00:00Z\"}");
        using GitHubPullRequestStatusClient closedWithoutMergeDateClient = CreateClient("{\"state\":\"closed\"}");

        PullRequestLifecycleState closed = await closedClient.GetStateAsync("owner/repo", 1, CancellationToken.None);
        PullRequestLifecycleState merged = await mergedClient.GetStateAsync("owner/repo", 2, CancellationToken.None);
        PullRequestLifecycleState closedWithoutMergeDate = await closedWithoutMergeDateClient.GetStateAsync("owner/repo", 3, CancellationToken.None);

        closed.Should().Be(PullRequestLifecycleState.Closed);
        merged.Should().Be(PullRequestLifecycleState.Merged);
        closedWithoutMergeDate.Should().Be(PullRequestLifecycleState.Closed);
    }

    [Fact]
    public async Task GetStateAsync_ShouldThrow_WhenGitHubReturnsError()
    {
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = new GitHubPullRequestStatusClient(new HttpClient(handler));

        Func<Task> act = () => client.GetStateAsync("owner/repo", 42, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*HTTP 404*");
    }

    public static TheoryData<HttpStatusCode, string?, string?, string?, DateTimeOffset> RateLimitResponses => new()
    {
        { HttpStatusCode.TooManyRequests, "60", null, null, _now.AddSeconds(60) },
        { HttpStatusCode.Forbidden, "Tue, 15 Sep 2026 01:00:00 GMT", "0", "1789430400", new DateTimeOffset(2026, 9, 15, 1, 0, 0, TimeSpan.Zero) },
        { HttpStatusCode.Forbidden, null, "0", "1789434000", DateTimeOffset.FromUnixTimeSeconds(1789434000) },
        { HttpStatusCode.TooManyRequests, null, "0", "1789434000", DateTimeOffset.FromUnixTimeSeconds(1789434000) },
    };

    [Theory]
    [MemberData(nameof(RateLimitResponses))]
    public async Task GetStateAsync_ShouldThrowRateLimitException_WhenResetTimeIsProvided(
        HttpStatusCode statusCode,
        string? retryAfter,
        string? remaining,
        string? reset,
        DateTimeOffset expectedResetAt)
    {
        using GitHubPullRequestStatusClient client = CreateErrorClient(statusCode, retryAfter, remaining, reset);

        Func<Task> act = () => client.GetStateAsync("owner/repo", 42, CancellationToken.None);

        (await act.Should().ThrowAsync<GitHubRateLimitException>())
            .Which.ResetAt.Should().Be(expectedResetAt);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, null, null, null)]
    [InlineData(HttpStatusCode.Forbidden, null, "10", "1789434000")]
    [InlineData(HttpStatusCode.TooManyRequests, null, "0", null)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "60", "0", "1789434000")]
    public async Task GetStateAsync_ShouldThrowGeneralHttpError_WhenResetTimeIsUnavailable(
        HttpStatusCode statusCode,
        string? retryAfter,
        string? remaining,
        string? reset)
    {
        using GitHubPullRequestStatusClient client = CreateErrorClient(statusCode, retryAfter, remaining, reset);

        Func<Task> act = () => client.GetStateAsync("owner/repo", 42, CancellationToken.None);

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.Should().NotBeOfType<GitHubRateLimitException>();
    }

    [Theory]
    [InlineData("{\"state\":\"unknown\"}")]
    [InlineData("{\"merged_at\":null}")]
    [InlineData("{\"state\":null}")]
    [InlineData("[]")]
    [InlineData("not-json")]
    public async Task GetStateAsync_ShouldThrow_WhenResponseIsInvalid(string content)
    {
        using GitHubPullRequestStatusClient client = CreateClient(content);

        Func<Task> act = () => client.GetStateAsync("owner/repo", 42, CancellationToken.None);

        await act.Should().ThrowAsync<JsonException>();
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("owner/repo", 0)]
    [InlineData("owner/repo/other", 1)]
    [InlineData("owner/repo?token=secret", 1)]
    public async Task GetStateAsync_ShouldThrow_WhenReferenceIsInvalid(string repository, int prNumber)
    {
        using GitHubPullRequestStatusClient client = CreateClient("{\"state\":\"open\"}");

        Func<Task> act = () => client.GetStateAsync(repository, prNumber, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenTimeoutIsNotPositive()
    {
        Action act = () => _ = new GitHubPullRequestStatusClient(requestTimeout: TimeSpan.Zero);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_ShouldPreserveExistingRequestHeaders()
    {
        using var httpClient = new HttpClient(new RecordingHandler(_ => JsonResponse("{\"state\":\"open\"}")));
        httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("existing-client", "1.0"));
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/custom"));

        using var client = new GitHubPullRequestStatusClient(httpClient);

        httpClient.DefaultRequestHeaders.UserAgent.Should().ContainSingle(value => value.Product != null && value.Product.Name == "existing-client");
        httpClient.DefaultRequestHeaders.Accept.Should().ContainSingle(value => value.MediaType == "application/custom");
    }

    [Fact]
    public async Task Dispose_ShouldDisposeOwnedHttpClient()
    {
        var client = new GitHubPullRequestStatusClient();
        client.Dispose();

        Func<Task> act = () => client.GetStateAsync("owner/repo", 42, CancellationToken.None);

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    private static GitHubPullRequestStatusClient CreateClient(string content)
        => new(new HttpClient(new RecordingHandler(_ => JsonResponse(content))));

    private static GitHubPullRequestStatusClient CreateErrorClient(
        HttpStatusCode statusCode,
        string? retryAfter,
        string? remaining,
        string? reset)
    {
        var handler = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(statusCode);
            AddHeaderIfPresent(response, "Retry-After", retryAfter);
            AddHeaderIfPresent(response, "x-ratelimit-remaining", remaining);
            AddHeaderIfPresent(response, "x-ratelimit-reset", reset);
            return response;
        });
        return new GitHubPullRequestStatusClient(new HttpClient(handler), timeProvider: new FixedTimeProvider(_now));
    }

    private static void AddHeaderIfPresent(HttpResponseMessage response, string name, string? value)
    {
        if (value is not null)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private static HttpResponseMessage JsonResponse(string content)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(_responseFactory(request));
        }
    }
}
